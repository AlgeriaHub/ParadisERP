using Microsoft.Extensions.Logging;

namespace ParadiERP.Infrastructure.Persistence;

/// <summary>
/// A one-time data correction run at start-up, kept from taking the whole site down with it.
///
/// <para>
/// The daily-pricing restatement refuses — throws — on any historical document it cannot trace,
/// and rolls its one transaction back. Unguarded at start-up, that refusal stopped the API
/// booting at all, for every company it serves, and on every restart after, because the queue it
/// refused is still there: one bad record and the site is down, with a deploy that reports
/// success. A refused correction leaves the books exactly as they were, which is a reason to say
/// so loudly, not a reason to stop the shop selling.
/// </para>
/// <para>
/// So the failure is logged as an error (Sentry takes it when a DSN is set), the company starts
/// without the correction, and the next start tries again. What the correction had changed in
/// memory before it gave up is still tracked on the context even though its transaction rolled
/// back; it is forgotten here, or the next step's save would write half a correction.
/// </para>
/// </summary>
public static class StartupCorrection
{
    /// <returns>What the correction returned, or <c>default</c> when it failed.</returns>
    public static async Task<T?> RunAsync<T>(
        PosDbContext db, ILogger logger, string correction, string company, Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogError(ex,
                "Start-up correction '{Correction}' failed for company {Company} and was rolled back. " +
                "The company starts without it; it is tried again on the next start.",
                correction, company);
            return default;
        }
    }
}
