using System.Globalization;
using System.Threading.RateLimiting;
using ParadisERP.API.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace ParadisERP.API.Security;

/// <summary>
/// How much one caller may ask of the API, and how big one request may be (security review,
/// 2026-10-10: "no API may be open to a script that submits requests without end").
///
/// <para>
/// The deployment has no proxy in front of it — SmarterASP serves the app straight from IIS —
/// so the limits live in the app, on ASP.NET Core's own rate limiter, and every one of them is a
/// setting (<c>RateLimits</c> in appsettings) rather than a constant. They are sized to stop a
/// script, never a shop: a whole branch behind one address, a back-office page firing a dozen
/// calls at once and a till catching up after a day offline all stay far below them.
/// </para>
/// <list type="bullet">
/// <item><b>Per address</b>, every API call: a token bucket, so a burst is fine and a flood is not.</item>
/// <item><b>Login</b> and the password change, per address: a fixed window, because each attempt
/// costs a BCrypt verification and those are what a flood turns into a stopped server.</item>
/// <item><b>Per session</b> (the bearer token), every signed-in call: one stolen token driven from
/// many addresses is still one caller.</item>
/// <item><b>Sync</b>, per session: the till's push and pull, generous enough for a day's backlog.</item>
/// <item><b>Receipts</b> sent by e-mail or SMS, per session: each one is a message the company pays for
/// and a reason for its mailbox to be blacklisted.</item>
/// <item><b>Uploads</b>, per session: files fill the disk.</item>
/// </list>
/// <para>
/// Rule one stands: a sale is never refused here. The till writes it to IndexedDB and prints the
/// receipt before it tries the network, and a refused push is simply tried again later.
/// </para>
/// </summary>
public static class RequestLimits
{
    public const string Login = "login";
    public const string Session = "session";
    public const string SyncPush = "sync-push";
    public const string SyncPull = "sync-pull";
    public const string Receipts = "receipts";
    public const string Uploads = "uploads";

    /// <summary>The stable code a refused request carries, for the screens to say it in the reader's language.</summary>
    public const string RateLimitedCode = "rateLimited";

    /// <summary>Every limit, from <c>RateLimits</c> in configuration; the defaults are the shipped values.</summary>
    public sealed class Options
    {
        /// <summary>Off only for test hosts that fire hundreds of calls a second from one address.</summary>
        public bool Enabled { get; set; } = true;

        public int PerAddressBurst { get; set; } = 120;
        public int PerAddressPerSecond { get; set; } = 30;
        public int LoginPerMinute { get; set; } = 20;
        public int SessionBurst { get; set; } = 120;
        public int SessionPerSecond { get; set; } = 10;
        public int SyncPushPerMinute { get; set; } = 60;
        public int SyncPullPerMinute { get; set; } = 120;
        public int ReceiptsPerMinute { get; set; } = 10;
        public int UploadsPerTenMinutes { get; set; } = 30;

        /// <summary>Any request body, unless its route says otherwise. A company logo in base64 fits.</summary>
        public long DefaultBodyBytes { get; set; } = 2 * 1024 * 1024;
        /// <summary>One file of an upload or an import preview: the largest is a 10 MB punch-clock export.</summary>
        public long UploadBodyBytes { get; set; } = 12 * 1024 * 1024;
        /// <summary>A till's push (up to 500 sales) and an import's confirmed rows.</summary>
        public long BulkBodyBytes { get; set; } = 8 * 1024 * 1024;
    }

    /// <summary>What a route may receive, set by <see cref="WithRequestLimits"/>.</summary>
    public sealed record BodyLimit(long Bytes);

    public static IServiceCollection AddRequestLimits(this IServiceCollection services, IConfiguration configuration, out Options options)
    {
        var limits = configuration.GetSection("RateLimits").Get<Options>() ?? new Options();
        options = limits;
        services.AddSingleton(limits);

        services.AddRateLimiter(rate =>
        {
            rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rate.OnRejected = (context, ct) => RejectAsync(context, ct);

            rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !limits.Enabled
                    ? RateLimitPartition.GetNoLimiter("off")
                    : RateLimitPartition.GetTokenBucketLimiter("ip:" + Company(http) + Address(http), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = limits.PerAddressBurst,
                        TokensPerPeriod = limits.PerAddressPerSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            rate.AddPolicy(Login, http => !limits.Enabled
                ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetFixedWindowLimiter("login:" + Company(http) + Address(http), _ => Window(limits.LoginPerMinute, TimeSpan.FromMinutes(1))));

            rate.AddPolicy(Session, http => !limits.Enabled
                ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetTokenBucketLimiter("session:" + Company(http) + Caller(http), _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.SessionBurst,
                    TokensPerPeriod = limits.SessionPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            rate.AddPolicy(SyncPush, http => PerCaller(http, limits, SyncPush, limits.SyncPushPerMinute, TimeSpan.FromMinutes(1)));
            rate.AddPolicy(SyncPull, http => PerCaller(http, limits, SyncPull, limits.SyncPullPerMinute, TimeSpan.FromMinutes(1)));
            rate.AddPolicy(Receipts, http => PerCaller(http, limits, Receipts, limits.ReceiptsPerMinute, TimeSpan.FromMinutes(1)));
            rate.AddPolicy(Uploads, http => PerCaller(http, limits, Uploads, limits.UploadsPerTenMinutes, TimeSpan.FromMinutes(10)));
        });
        return services;
    }

    /// <summary>
    /// Gives every route of the API its limit and its body size, decided here in one place so a
    /// route added later is limited by default (the session policy) rather than forgotten.
    /// </summary>
    public static RouteGroupBuilder WithRequestLimits(this RouteGroupBuilder group, Options options)
    {
        ((IEndpointConventionBuilder)group).Finally(builder =>
        {
            if (builder is not RouteEndpointBuilder route) return;
            var pattern = RoutePermissionConvention.Pattern(route);
            var methods = builder.Metadata.OfType<HttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
            var (policy, body) = Classify(methods, pattern, options);
            builder.Metadata.Add(new EnableRateLimitingAttribute(policy));
            builder.Metadata.Add(new BodyLimit(body));
        });
        return group;
    }

    /// <summary>The policy and the largest body for one route. Public for the route-inventory tests.</summary>
    public static (string Policy, long BodyBytes) Classify(IReadOnlyList<string> methods, string pattern, Options options)
    {
        var post = methods.Contains("POST");
        if (pattern is "/api/auth/login" or "/api/auth/password") return (Login, options.DefaultBodyBytes);
        // Every step that checks a password or a code is held to the sign-in's pace.
        if (post && pattern.StartsWith("/api/auth/two-factor/", StringComparison.Ordinal)) return (Login, options.DefaultBodyBytes);
        if (pattern == "/api/sync/push") return (SyncPush, options.BulkBodyBytes);
        if (pattern == "/api/sync/pull") return (SyncPull, options.DefaultBodyBytes);
        if (pattern.EndsWith("/send-receipt", StringComparison.Ordinal)) return (Receipts, options.DefaultBodyBytes);
        if (post && (pattern.EndsWith("/attachments", StringComparison.Ordinal) || pattern.EndsWith("/import/preview", StringComparison.Ordinal)))
            return (Uploads, options.UploadBodyBytes);
        if (post && pattern.Contains("/import", StringComparison.Ordinal)) return (Session, options.BulkBodyBytes);
        return (Session, options.DefaultBodyBytes);
    }

    /// <summary>
    /// Holds each request to its route's body size, before anything reads the body. Kestrel's own
    /// ceiling is the largest any route allows; this lowers it per route, so a login or a list
    /// cannot be sent 28 MB to keep in memory.
    /// </summary>
    public static IApplicationBuilder UseRequestBodyLimits(this IApplicationBuilder app, Options options) =>
        app.Use(async (context, next) =>
        {
            var limit = context.GetEndpoint()?.Metadata.GetMetadata<BodyLimit>()?.Bytes ?? options.DefaultBodyBytes;
            // A body that says up front it is too big is refused before a byte of it is read.
            if (context.Request.ContentLength is long declared && declared > limit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Request too large",
                    status = 413,
                    detail = $"This request is limited to {limit / 1024} KB.",
                    code = "requestTooLarge",
                }, context.RequestAborted);
                return;
            }
            // One that does not say (chunked) is cut off by the server once it passes the limit.
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
            await next(context);
        });

    private static RateLimitPartition<string> PerCaller(HttpContext http, Options limits, string name, int permits, TimeSpan window) =>
        !limits.Enabled
            ? RateLimitPartition.GetNoLimiter("off")
            : RateLimitPartition.GetFixedWindowLimiter(name + ":" + Company(http) + Caller(http), _ => Window(permits, window));

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) => new()
    {
        PermitLimit = Math.Max(1, permits),
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    /// <summary>The hostname is the company: two companies behind one address never share a limit.</summary>
    private static string Company(HttpContext http) => http.Request.Host.Host.ToLowerInvariant() + "|";

    /// <summary>
    /// The caller's address. Out of process under IIS, the module forwards the client's address and
    /// the app's IIS integration applies it, so this is the browser's, not the module's loopback.
    /// </summary>
    private static string Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// The session, by its token's signature (the part after the last dot), read before the token is
    /// validated so a refused request costs no database work; an anonymous call falls back to the
    /// address. A forged token only partitions itself and is refused by authentication anyway.
    /// </summary>
    private static string Caller(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header[7..].Trim();
            var dot = token.LastIndexOf('.');
            var signature = dot >= 0 ? token[(dot + 1)..] : token;
            if (signature.Length > 0) return "t:" + (signature.Length > 64 ? signature[^64..] : signature);
        }
        return "a:" + Address(http);
    }

    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken ct)
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await response.WriteAsJsonAsync(new
        {
            title = "Too many requests",
            status = 429,
            detail = $"Too many requests. Try again in {seconds} seconds.",
            code = RateLimitedCode,
            retryAfterSeconds = seconds,
        }, ct);
    }
}
