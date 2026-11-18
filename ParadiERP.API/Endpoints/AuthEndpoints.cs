using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Application.Regional;
using ParadiERP.Infrastructure.Integrations;
using ParadiERP.Infrastructure.Tenancy;
using ParadiERP.Application.Subscriptions;

namespace ParadiERP.API.Endpoints;

public static class AuthEndpoints
{
    /// <summary>
    /// A real hash of a value nobody knows, verified against when the username does not exist.
    ///
    /// <para>
    /// Without it the reply is fast for a name the company has never heard of and slow — a
    /// whole BCrypt verification — for one it has, which tells anyone who asks exactly which
    /// accounts are worth guessing at. The dummy costs the same work as the real check, so both
    /// answers take the same time and say the same thing.
    /// </para>
    /// </summary>
    internal static readonly string NoSuchAccount = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest request, PosDbContext db, TokenService tokens, ISubscriptionProvider subscriptions,
            IRegionalSettingsProvider regional, ILanguagePackStore languagePacks, TenantIntegrations integrations,
            LoginThrottle throttle, TenantContext tenant, [FromServices] TwoFactor twoFactor, HttpContext http, CancellationToken ct) =>
        {
            // A username longer than any account can have, or a password longer than any rule needs,
            // is refused before anything else: kept in memory and sent to the database, one of a few
            // megabytes was a way to fill both (security review, 2026-10-10).
            if (request.Username is { Length: > MaxUsernameLength } || request.Password is { Length: > MaxPasswordLength })
                return Coded(StatusCodes.Status400BadRequest, "credentialsTooLong", "The username or the password is too long.");

            // Reserved before anything is read or hashed: a refused attempt has to be the cheap one,
            // or the cap protects the password and not the server it runs on. Per account and per
            // address, so guessing at the administrator from outside does not lock them out.
            var address = http.Connection.RemoteIpAddress?.ToString();
            if (throttle.TryBegin(tenant.Current.Id, request.Username, address) is TimeSpan wait)
                return TooManyAttempts(wait);

            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Username == request.Username && !u.IsDeleted, ct);

            // Always one verification, whoever asked. A deactivated account goes through it too,
            // so "wrong password" and "that account is switched off" are not distinguishable
            // from outside either.
            var usable = user is { IsActive: true } && user.PasswordHash.Length > 0;
            var verified = await VerifyAsync(request.Password, usable ? user!.PasswordHash : NoSuchAccount, ct);
            if (verified is null) return Busy();
            if (verified == false || !usable || user is null)
                return Results.Unauthorized();   // the reserved attempt stays counted

            // Two-step login (owner, 2026-11-11). With it on, the right password alone does not give
            // the attempt back: only the right code does, so codes are guessed no faster than passwords.
            var secondStep = TwoFactor.IsOn(user);
            if (!secondStep) throttle.Succeeded(tenant.Current.Id, request.Username, address);

            // The till checks come first, so nobody types a code only to be told it is the wrong till.
            async Task<IResult> OpenAsync(string code, Guid? branchId)
            {
                string? trust = null;
                if (secondStep)
                {
                    var (outcome, token) = await twoFactor.CheckSignInAsync(
                        db, user, request.TwoFactorCode, request.TrustToken, request.RememberDevice, ct);
                    if (outcome == TwoFactorOutcome.Required)
                        return Coded(StatusCodes.Status401Unauthorized, TwoFactorRequiredCode, TwoFactorRequiredMessage);
                    if (outcome == TwoFactorOutcome.Wrong)
                        return Coded(StatusCodes.Status401Unauthorized, TwoFactorWrongCode, TwoFactorWrongMessage);
                    throttle.Succeeded(tenant.Current.Id, request.Username, address);
                    trust = token;
                    // A new trusted computer may have drawn the account's first stamp.
                    if (trust is not null) await db.SaveChangesAsync(ct);
                }
                var session = await SessionAsync(user, code, branchId, db, tokens, subscriptions, regional, languagePacks, integrations, ct);
                return Results.Ok(trust is null ? session : session with { TwoFactorTrust = trust });
            }

            // AUTH-1: what this account may do, from its role, decided before the terminal is.
            var decided = await ParadiERP.Infrastructure.Authorization.ServerAuthorization.ResolveAsync(db, user.Id, ct);
            if (decided is null)
                return Results.Problem("This account has no role. Ask the administrator.", statusCode: StatusCodes.Status403Forbidden);

            // No terminal code. For a role that reads beyond a till's branch, an office sign-in, to work
            // in the back office from a computer that is not a till (owner, 2026-10-05). For a
            // till-scoped account, their own till: the code is never typed online (owner, 2026-10-05:
            // "no need to sign in with a till code except offline").
            string deviceCode;
            if (string.IsNullOrWhiteSpace(request.DeviceCode))
            {
                if (BranchScope.AllowsOfficeSession(decided.Scope))
                    return await OpenAsync("", null);

                var own = await CashierTills.ForSignInAsync(db, user, decided.Scope, ct);
                if (own is null)
                    return Results.Problem(CashierTills.NoOwnTillMessage, statusCode: StatusCodes.Status403Forbidden,
                        extensions: new Dictionary<string, object?> { ["code"] = BranchScope.TillRequiredCode });
                deviceCode = own.Code;
            }
            else
            {
                deviceCode = request.DeviceCode.Trim().ToUpperInvariant();
            }

            // Terminals are registered in the back office, never auto-created here:
            // auto-provisioning would let anyone whose till was deactivated simply log in
            // with a new device code and carry on.
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Code == deviceCode, ct);
            if (device is null)
                return Results.Problem(
                    $"Terminal '{deviceCode}' is not registered. Add it in the back office first.",
                    statusCode: StatusCodes.Status403Forbidden);

            if (!device.IsActive)
                return Results.Problem("This terminal has been deactivated.", statusCode: StatusCodes.Status403Forbidden);

            // A cashier bound to a branch opens sessions on that branch's tills only; typing
            // another branch's code here would otherwise make them that branch's terminal for
            // every scoped read and for the receipt-number space (docs/07 §2.2). Managers and
            // admins are never refused here: their branch locks the screens (BranchLock), not the login.
            // AUTH-1: the binding follows the role's scope (TillBranch), not its name.
            if (BranchScope.RefusesTerminal(user, decided.Scope, device))
                return Results.Problem(BranchScope.WrongTerminalMessage, statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["code"] = BranchScope.WrongTerminalCode });

            return await OpenAsync(deviceCode, device.BranchId);
        })
        .AllowAnonymous()
        .WithName("Login");

        // A signed-in person changes their own password (owner, 2026-10-05: "keep the password created
        // from admin side, and the user after login can change password"). The administrator's routes
        // are untouched: they still type the first password and any reset. Whoever holds a session may
        // ask — a cashier on a till as much as an administrator in the office (RoutePermissions: signed in).
        group.MapPost("/password", async (ChangePasswordRequest? request, ClaimsPrincipal caller, PosDbContext db,
            TokenService tokens, ISubscriptionProvider subscriptions, IRegionalSettingsProvider regional,
            ILanguagePackStore languagePacks, TenantIntegrations integrations, LoginThrottle throttle, TenantContext tenant,
            HttpContext http, CancellationToken ct) =>
        {
            // The session check has already accepted this token against the row, read on this request;
            // a user who vanished in between is simply not signed in any more.
            if (!Guid.TryParse(caller.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
                return Results.Unauthorized();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted && u.IsActive, ct);
            if (user is null) return Results.Unauthorized();

            // The cheap refusals first: neither needs a hash, so neither counts as a guess.
            var current = request?.CurrentPassword ?? "";
            var next = request?.NewPassword;
            if (current.Length > MaxPasswordLength || next is { Length: > MaxPasswordLength })
                return Coded(StatusCodes.Status400BadRequest, "credentialsTooLong", "The password is too long.");
            if (!PasswordRule.IsAcceptable(next))
                return Coded(StatusCodes.Status400BadRequest, PasswordTooShortCode, PasswordRule.TooShortMessage);
            if (string.Equals(next, current, StringComparison.Ordinal))
                return Coded(StatusCodes.Status400BadRequest, PasswordSameCode, PasswordSameMessage);

            // The current password is a password guess like any other, against the same account: it
            // goes through the login's own cap (LoginThrottle), keyed by the name the account signs in
            // with, so a stolen session cannot be used to try passwords faster than the login allows.
            var address = http.Connection.RemoteIpAddress?.ToString();
            if (throttle.TryBegin(tenant.Current.Id, user.Username, address) is TimeSpan wait)
                return TooManyAttempts(wait);
            // An account with no password of its own (SSO only) has nothing to verify against; the dummy
            // costs the same work, as on the login.
            var usable = user.PasswordHash.Length > 0;
            var verified = await VerifyAsync(current, usable ? user.PasswordHash : NoSuchAccount, ct);
            if (verified is null) return Busy();
            if (verified == false || !usable)
                return Coded(StatusCodes.Status400BadRequest, PasswordWrongCode, PasswordWrongMessage);
            throttle.Succeeded(tenant.Current.Id, user.Username, address);

            // The session continues where the token was opened: the same till, or the office. Read
            // before anything is written, so a till switched off in between refuses the change rather
            // than leaving it saved with no session to hand back.
            var deviceCode = caller.FindFirstValue("device") ?? "";
            Guid? deviceBranchId = null;
            if (deviceCode.Length > 0)
            {
                var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Code == deviceCode && d.IsActive, ct);
                if (device is null) return Results.Unauthorized();
                deviceBranchId = device.BranchId;
            }

            // SaveWithVersionsAsync bumps the row's Version, which revokes every session the account
            // holds — on other computers and this one; the caller carries on with the token below. The
            // password itself goes nowhere but into the hash: never logged, never echoed.
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(next);
            // A new password forgets every computer trusted for two-step login.
            user.TwoFactorTrustStamp = TwoFactor.NewStamp();
            await db.SaveWithVersionsAsync(ct, user);
            return Results.Ok(await SessionAsync(user, deviceCode, deviceBranchId,
                db, tokens, subscriptions, regional, languagePacks, integrations, ct));
        })
        .WithName("ChangePassword");

        // Anonymous: the login screen needs the translation before anyone signs in. Only packs the
        // company actually enables are served.
        app.MapGet("/api/i18n/{code}", async (string code, IRegionalSettingsProvider regional,
            ILanguagePackStore languagePacks, CancellationToken ct) =>
        {
            if (!regional.Get().Languages.Contains(code)) return Results.NotFound();
            var pack = await languagePacks.GetAsync(code, ct);
            return pack is null ? Results.NotFound() : Results.Ok(pack);
        }).AllowAnonymous().WithTags("Auth");

        // Signing out ends the session on the server too (owner, security review 2026-10-10). The
        // client only forgot its copy, and a token taken from a shared computer stayed good until it
        // expired. One version bump on the row ends every session of this account at once — there
        // is no per-session store — which is what a sign-out from a shared computer should do.
        // A till cashier's sign-out never calls it: the till keeps that token for the offline PIN.
        group.MapPost("/logout", async (ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            if (Guid.TryParse(caller.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
                && await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct) is User user)
                await db.SaveWithVersionsAsync(ct, user);
            return Results.NoContent();
        });

        group.MapGet("/ping", () => Results.Ok(new { ok = true, serverTimeUtc = DateTime.UtcNow }))
            .AllowAnonymous()
            .WithName("Ping");
    }

    public const string TwoFactorRequiredCode = "two_factor_required";
    public const string TwoFactorRequiredMessage = "Enter the code from your authenticator app.";
    public const string TwoFactorWrongCode = "two_factor_wrong";
    public const string TwoFactorWrongMessage = "The code is not correct, or it was already used. Wait for the next code and try again.";

    public const string PasswordWrongCode = "password_wrong";
    public const string PasswordWrongMessage = "The current password is not correct.";
    public const string PasswordTooShortCode = "password_too_short";
    public const string PasswordSameCode = "password_same";
    public const string PasswordSameMessage = "The new password must be different from the current one.";

    /// <summary>
    /// The body every password session is answered with — the sign-in, and the change of password that
    /// replaces the session it was asked from — so the second can never drift from the first. An empty
    /// <paramref name="deviceCode"/> is an office session: no till, no till's branch, no receipt sequence.
    /// </summary>
    private static async Task<LoginResponse> SessionAsync(User user, string deviceCode, Guid? deviceBranchId,
        PosDbContext db, TokenService tokens, ISubscriptionProvider subscriptions, IRegionalSettingsProvider regional,
        ILanguagePackStore languagePacks, TenantIntegrations integrations, CancellationToken ct)
    {
        var loginUser = await BranchAssignment.LoginUserAsync(user, db, ct);
        var (token, expires) = tokens.Create(user, deviceCode, loginUser.Permissions ?? []);
        return new LoginResponse(
            token, expires,
            loginUser,
            deviceCode, subscriptions.GetAccess(), regional.Get(),
            await languagePacks.RevisionsForAsync(regional.Get(), ct),
            await integrations.PublicViewAsync(ct), deviceBranchId,
            deviceCode.Length == 0 ? 0 : await CashierTills.LastSequenceAsync(db, deviceCode, ct));
    }

    /// <summary>The cap's answer (LoginThrottle), the same on the sign-in and on the change of password.</summary>
    internal static IResult TooManyAttempts(TimeSpan wait)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return Results.Problem("Too many sign-in attempts for this account. Try again shortly.",
            statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts",
            extensions: new Dictionary<string, object?> { ["code"] = "too_many_attempts", ["retryAfterSeconds"] = seconds });
    }

    /// <summary>A refusal with a stable code the screen translates, and the sentence for everyone else.</summary>
    internal static IResult Coded(int status, string code, string message) =>
        Results.Problem(message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>The longest username an account can have (User.Username, MaxLength 64).</summary>
    public const int MaxUsernameLength = 64;
    /// <summary>Far above any password rule; BCrypt reads only the first 72 bytes anyway.</summary>
    public const int MaxPasswordLength = 128;

    /// <summary>
    /// How many BCrypt verifications may run at once (security review, 2026-10-10). Each one takes
    /// a core for about a tenth of a second; without a ceiling a burst of sign-ins took every core
    /// and the tills could not sync. Waiting is short, and a sign-in that cannot start is told so.
    /// </summary>
    private static readonly SemaphoreSlim Verifications = new(Math.Max(2, Environment.ProcessorCount));

    /// <summary>True or false, or null when the server is too busy hashing to start another check.</summary>
    internal static async Task<bool?> VerifyAsync(string? password, string hash, CancellationToken ct)
    {
        if (!await Verifications.WaitAsync(TimeSpan.FromSeconds(10), ct)) return null;
        try { return Verify(password, hash); }
        finally { Verifications.Release(); }
    }

    internal static IResult Busy() =>
        Results.Problem("The server is busy. Try again in a moment.", statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Busy", extensions: new Dictionary<string, object?> { ["code"] = "busy", ["retryAfterSeconds"] = 5 });

    /// <summary>
    /// A stored hash that BCrypt cannot parse is a corrupt row, not a reason to answer a login
    /// with a 500 — which would both take the API down for that account and say, loudly, that
    /// the account is there.
    /// </summary>
    private static bool Verify(string? password, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(password ?? "", hash); }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }
}
