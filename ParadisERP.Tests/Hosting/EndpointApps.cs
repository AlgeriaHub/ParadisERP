using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ParadisERP.Tests.Hosting;

/// <summary>
/// The minimal applications the endpoint harnesses call routes through, built once per test
/// database and kept for that test's later calls.
/// <para>
/// A harness used to build a whole <see cref="WebApplication"/> — configuration, the services, every
/// route of the module compiled — for each single call, and throw it away: a test that made thirty
/// calls built it thirty times, most of a manufacturing test's run time. An application is now kept
/// per context (held weakly: it goes with the test's database) and per whatever else shaped its
/// services (the key), and each call still runs in a request scope of its own, so no scoped service
/// is shared between two calls. Only what was registered as a single instance is shared, and every
/// such registration in the harnesses is stateless or is the test's own object.
/// </para>
/// </summary>
internal static class EndpointApps
{
    private static readonly ConditionalWeakTable<PosDbContext, ConcurrentDictionary<object, Built>> Apps = new();

    internal sealed class Built(WebApplication app)
    {
        public WebApplication App { get; } = app;
        private readonly ConcurrentDictionary<(string Method, string Pattern), RouteEndpoint> endpoints = new();

        /// <summary>The route answering <paramref name="method"/> on <paramref name="pattern"/>; exactly one, as before.</summary>
        public RouteEndpoint Endpoint(string method, string pattern) =>
            endpoints.GetOrAdd((method, pattern), key => ((IEndpointRouteBuilder)App).DataSources
                .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText!.TrimEnd('/') == key.Pattern
                    && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(key.Method)));
    }

    /// <summary>
    /// The application for <paramref name="db"/> and <paramref name="key"/>, built by
    /// <paramref name="build"/> the first time. The key must name everything else the build reads:
    /// two calls with the same key must want the same application.
    /// </summary>
    public static Built For(PosDbContext db, object key, Func<WebApplication> build) =>
        Apps.GetOrCreateValue(db).GetOrAdd(key, _ => new Built(build()));
}
