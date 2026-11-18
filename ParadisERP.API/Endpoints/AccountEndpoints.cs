using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Files;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Rentals;
using ParadisERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// The signed-in user's own account (owner, 2026-11-11): "My profile" — who they are, read-only, and
/// their photo, which they may change — and their two-step login, which they alone turn on or off.
/// Every route reads and writes the caller's own row only; none takes a user id.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>A profile photo is small: the screen shrinks it before sending.</summary>
    public const int MaxPhotoBytes = 1024 * 1024;

    public const string TwoFactorAlreadyOnCode = "two_factor_already_on";
    public const string TwoFactorOffCode = "two_factor_off";
    public const string TwoFactorNoSetupCode = "two_factor_no_setup";
    public const string PhotoInvalidCode = "photo_invalid";
    public const string PhotoTooLargeCode = "photo_too_large";

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Account");

        // ---------- profile ----------

        group.MapGet("/profile", async (ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: false);
            if (user is null) return Results.Unauthorized();

            var branch = await db.Branches.AsNoTracking()
                .Where(b => user.BranchId != null ? b.Id == user.BranchId : b.IsMain && !b.IsWarehouse && !b.IsDeleted)
                .Select(b => new { b.NameAr, b.NameFr, b.NameEn })
                .FirstOrDefaultAsync(ct);
            var roleName = user.RoleId is Guid roleId
                ? await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => r.Name).FirstOrDefaultAsync(ct)
                : null;
            var employee = user.EmployeeId is Guid employeeId
                ? await db.Employees.AsNoTracking().Where(e => e.Id == employeeId)
                    .Select(e => new ProfileEmployeeDto(e.Number, e.JobTitle, e.Phone, e.Email, e.HiredOn))
                    .FirstOrDefaultAsync(ct)
                : null;

            return Results.Ok(new ProfileDto(
                user.Id, user.FullName, user.Username, user.LoginId, user.Role.ToString(), roleName,
                branch?.NameAr, branch?.NameFr, branch?.NameEn,
                employee,
                user.PhotoPath is { } path ? FilePaths.Version(path) : null,
                Status(user)));
        });

        group.MapGet("/profile/photo", async (ClaimsPrincipal caller, PosDbContext db, IFileStore files, HttpContext http, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: false);
            if (user is null) return Results.Unauthorized();
            // The same answer as a logo: the link carries the photo's version, so it may be kept privately.
            return await FileResults.LogoAsync(files, user.PhotoPath, http, ct);
        });

        group.MapPost("/profile/photo", async (HttpRequest request, ClaimsPrincipal caller, PosDbContext db, IFileStore files, CancellationToken ct) =>
        {
            if (request.ContentLength is long declared && declared > MaxPhotoBytes + 64 * 1024)
                return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, PhotoTooLargeCode, "The photo is larger than 1 MB.");
            if (!request.HasFormContentType)
                return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, PhotoInvalidCode, "Send the photo as multipart/form-data.");

            var user = await CallerAsync(caller, db, ct, tracked: true);
            if (user is null) return Results.Unauthorized();

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
                return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, PhotoInvalidCode, "Send exactly one photo.");
            var file = form.Files[0];
            if (file.Length > MaxPhotoBytes)
                return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, PhotoTooLargeCode, "The photo is larger than 1 MB.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var content = buffer.ToArray();

            // Read from the file's own first bytes, never from what the browser claims. A picture only:
            // no PDF, and no SVG — a user's own upload is never a document that can carry script.
            var type = RentalDocuments.SniffContentType(content);
            if (content.Length == 0 || type is not ("image/jpeg" or "image/png" or "image/webp"))
                return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, PhotoInvalidCode, "Only a JPEG, PNG or WebP picture can be a profile photo.");

            // The file before the row, as everywhere: a failed save leaves a file nothing points at,
            // never a row pointing at nothing. The old photo goes once the row no longer names it.
            var path = FilePaths.UserPhoto(user.Id, content, type);
            await files.WriteAsync(path, content, ct);
            var previous = user.PhotoPath;
            user.PhotoPath = path;
            // A plain save: the photo is not replicated and changes no session.
            await db.SaveChangesAsync(ct);
            if (previous is not null && previous != path) await files.DeleteAsync(previous, ct);
            return Results.Ok(new { photoVersion = FilePaths.Version(path) });
        });

        group.MapDelete("/profile/photo", async (ClaimsPrincipal caller, PosDbContext db, IFileStore files, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: true);
            if (user is null) return Results.Unauthorized();
            var previous = user.PhotoPath;
            user.PhotoPath = null;
            await db.SaveChangesAsync(ct);
            if (previous is not null) await files.DeleteAsync(previous, ct);
            return Results.NoContent();
        });

        // ---------- two-step login ----------

        group.MapGet("/two-factor", async (ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: false);
            return user is null ? Results.Unauthorized() : Results.Ok(Status(user));
        });

        // Step one: the password again, then a new key — kept as pending, nothing is on yet. Asking
        // again replaces the key offered before.
        group.MapPost("/two-factor/setup", async (PasswordConfirmRequest? request, ClaimsPrincipal caller, PosDbContext db,
            [FromServices] TwoFactor twoFactor, LoginThrottle throttle, TenantContext tenant, HttpContext http, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: true);
            if (user is null) return Results.Unauthorized();
            if (TwoFactor.IsOn(user))
                return AuthEndpoints.Coded(StatusCodes.Status409Conflict, TwoFactorAlreadyOnCode, "Two-step login is already on.");
            if (await ConfirmPasswordAsync(user, request?.Password, throttle, tenant, http, ct) is { } refused) return refused;

            var (secret, protectedKey) = twoFactor.NewKey();
            user.TwoFactorPendingSecret = protectedKey;
            await db.SaveChangesAsync(ct);

            var issuer = await IssuerAsync(db, ct);
            return Results.Ok(new TwoFactorSetupDto(Grouped(secret), Totp.ProvisioningUri(issuer, user.Username, secret), issuer, user.Username));
        });

        // Step two: a code from the app proves it holds the key. Then it is on, with ten recovery codes
        // shown this once, and every computer trusted before is forgotten.
        group.MapPost("/two-factor/enable", async (TwoFactorCodeRequest? request, ClaimsPrincipal caller, PosDbContext db,
            [FromServices] TwoFactor twoFactor, LoginThrottle throttle, TenantContext tenant, HttpContext http, TimeProvider time, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: true);
            if (user is null) return Results.Unauthorized();
            if (TwoFactor.IsOn(user))
                return AuthEndpoints.Coded(StatusCodes.Status409Conflict, TwoFactorAlreadyOnCode, "Two-step login is already on.");
            var key = twoFactor.Unprotect(user.TwoFactorPendingSecret);
            if (key is null)
                return AuthEndpoints.Coded(StatusCodes.Status409Conflict, TwoFactorNoSetupCode, "Start the setup again: there is no key waiting for a code.");

            var address = http.Connection.RemoteIpAddress?.ToString();
            if (throttle.TryBegin(tenant.Current.Id, user.Username, address) is TimeSpan wait)
                return AuthEndpoints.TooManyAttempts(wait);
            if (Totp.Match(key, request?.Code, time.GetUtcNow(), 0) is not long step)
                return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, AuthEndpoints.TwoFactorWrongCode, AuthEndpoints.TwoFactorWrongMessage);
            throttle.Succeeded(tenant.Current.Id, user.Username, address);

            var (codes, stored) = RecoveryCodes.Generate();
            user.TwoFactorSecret = user.TwoFactorPendingSecret;
            user.TwoFactorPendingSecret = null;
            user.TwoFactorEnabledAtUtc = time.GetUtcNow().UtcDateTime;
            user.TwoFactorLastStep = step;
            user.TwoFactorRecoveryCodes = stored;
            user.TwoFactorTrustStamp = TwoFactor.NewStamp();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RecoveryCodesDto(codes));
        });

        group.MapPost("/two-factor/disable", async (PasswordConfirmRequest? request, ClaimsPrincipal caller, PosDbContext db,
            LoginThrottle throttle, TenantContext tenant, HttpContext http, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: true);
            if (user is null) return Results.Unauthorized();
            if (!TwoFactor.IsOn(user))
                return AuthEndpoints.Coded(StatusCodes.Status409Conflict, TwoFactorOffCode, "Two-step login is already off.");
            if (await ConfirmPasswordAsync(user, request?.Password, throttle, tenant, http, ct) is { } refused) return refused;
            TwoFactor.Clear(user);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Status(user));
        });

        // New recovery codes replace every old one, used or not.
        group.MapPost("/two-factor/recovery-codes", async (PasswordConfirmRequest? request, ClaimsPrincipal caller, PosDbContext db,
            LoginThrottle throttle, TenantContext tenant, HttpContext http, CancellationToken ct) =>
        {
            var user = await CallerAsync(caller, db, ct, tracked: true);
            if (user is null) return Results.Unauthorized();
            if (!TwoFactor.IsOn(user))
                return AuthEndpoints.Coded(StatusCodes.Status409Conflict, TwoFactorOffCode, "Two-step login is off.");
            if (await ConfirmPasswordAsync(user, request?.Password, throttle, tenant, http, ct) is { } refused) return refused;
            var (codes, stored) = RecoveryCodes.Generate();
            user.TwoFactorRecoveryCodes = stored;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RecoveryCodesDto(codes));
        });
    }

    private static TwoFactorStatusDto Status(User user) =>
        new(TwoFactor.IsOn(user), user.TwoFactorEnabledAtUtc, TwoFactor.IsOn(user) ? RecoveryCodes.Left(user.TwoFactorRecoveryCodes) : 0);

    /// <summary>The caller's own row; the session check has already accepted the token against it.</summary>
    private static async Task<User?> CallerAsync(ClaimsPrincipal caller, PosDbContext db, CancellationToken ct, bool tracked)
    {
        if (!Guid.TryParse(caller.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)) return null;
        var users = tracked ? db.Users : db.Users.AsNoTracking();
        return await users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted && u.IsActive, ct);
    }

    /// <summary>
    /// The current password, checked as the change of password checks it: a guess like any sign-in,
    /// held to the same cap, so a session left open is not enough to change how the account signs in.
    /// </summary>
    private static async Task<IResult?> ConfirmPasswordAsync(User user, string? password, LoginThrottle throttle,
        TenantContext tenant, HttpContext http, CancellationToken ct)
    {
        if (password is { Length: > AuthEndpoints.MaxPasswordLength })
            return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, "credentialsTooLong", "The password is too long.");
        var address = http.Connection.RemoteIpAddress?.ToString();
        if (throttle.TryBegin(tenant.Current.Id, user.Username, address) is TimeSpan wait)
            return AuthEndpoints.TooManyAttempts(wait);
        var usable = user.PasswordHash.Length > 0;
        var verified = await AuthEndpoints.VerifyAsync(password ?? "", usable ? user.PasswordHash : AuthEndpoints.NoSuchAccount, ct);
        if (verified is null) return AuthEndpoints.Busy();
        if (verified == false || !usable)
            return AuthEndpoints.Coded(StatusCodes.Status400BadRequest, AuthEndpoints.PasswordWrongCode, AuthEndpoints.PasswordWrongMessage);
        throttle.Succeeded(tenant.Current.Id, user.Username, address);
        return null;
    }

    /// <summary>The name the authenticator app shows above the code: the company's, else a plain word.</summary>
    private static async Task<string> IssuerAsync(PosDbContext db, CancellationToken ct)
    {
        var store = await db.Stores.AsNoTracking().Select(s => new { s.NameFr, s.NameEn, s.NameAr }).FirstOrDefaultAsync(ct);
        var name = new[] { store?.NameFr, store?.NameEn, store?.NameAr }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();
        // A colon would split the app's label in two.
        return string.IsNullOrEmpty(name) ? "POS" : name.Replace(':', ' ');
    }

    /// <summary>The key in groups of four, easier to copy by hand; the apps ignore the spaces.</summary>
    private static string Grouped(string secret) =>
        string.Join(' ', Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - i * 4))));
}
