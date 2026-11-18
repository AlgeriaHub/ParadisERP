using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ParadisERP.Tests.Hosting;

/// <summary>
/// The real <c>Program</c>, started in this process against a SQLite file of its own
/// (docs/07 backlog item 4). Until this existed nothing in the suite could assert a status
/// code, a response header or a startup refusal — the rules lived in <c>Program.cs</c> and were
/// only ever exercised by deploying.
/// <para>
/// Configuration goes in through environment variables rather than
/// <c>IWebHostBuilder.UseSetting</c>, for one reason that is not a preference: the environment
/// name has to be <c>Production</c> before <c>WebApplication.CreateBuilder</c> returns, because
/// <c>Program</c> asks <c>builder.Environment.IsDevelopment()</c> for the SQLite guard and for
/// whether the demo company is seeded, and by the time a web-host setting can be applied that
/// question has already been answered. <c>Program</c> re-adds the environment after every file
/// source, so the rest follows the same path a deployment uses. The variables are set only
/// while the host is being built and then put back, and <see cref="HostCollection"/> keeps
/// these tests from building two hosts at once.
/// </para>
/// </summary>
public sealed class PosHost : WebApplicationFactory<Program>
{
    private static readonly object Gate = new();

    /// <summary>The temporary directory holding this host's database, web root and key ring.</summary>
    public string Root { get; }

    public string ConnectionString { get; }

    /// <summary>The directory the SQLite file sits in — deleting it is how a test takes the database away.</summary>
    public string DatabaseDirectory { get; }

    /// <summary>
    /// The Login ID the server drew for each account a test created under a readable name
    /// (<see cref="TestUsers"/>). Accounts sign in with the Login ID since 2026-10-05; a test still
    /// says "gerant-a", and <see cref="SignInName"/> turns that into the number.
    /// </summary>
    private readonly Dictionary<string, string> loginIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers the Login ID an account created under <paramref name="name"/> signs in with.</summary>
    public void Remember(string name, string loginId)
    {
        lock (loginIds) loginIds[name] = loginId;
    }

    /// <summary>What to type to sign in as <paramref name="name"/>: its Login ID, or the name itself for an account no test created.</summary>
    public string SignInName(string name)
    {
        lock (loginIds) return loginIds.TryGetValue(name, out var id) ? id : name;
    }

    /// <summary>The files folder's root; the company's own folder is <c>default</c> under it.</summary>
    public string FilesRoot { get; }

    /// <summary>Service replacements applied after Program's own registrations (a clock, a profile).</summary>
    private readonly Action<IServiceCollection>? services;

    /// <param name="settings">Environment entries to add, or to remove by passing a null value.</param>
    /// <param name="root">Reuse another host's directory, so this one starts on its database.</param>
    /// <param name="services">
    /// Replaces registrations after Program has made them — a test's <see cref="TimeProvider"/>, for
    /// one. Applied while the host is built here, in the constructor, like the environment.
    /// </param>
    public PosHost(IDictionary<string, string?>? settings = null, string? root = null, Action<IServiceCollection>? services = null)
        : this(settings, root, services, keepRoot: false)
    {
    }

    private PosHost(IDictionary<string, string?>? settings, string? root, Action<IServiceCollection>? services, bool keepRoot)
    {
        this.services = services;
        this.keepRoot = keepRoot;
        Root = root ?? Path.Combine(Path.GetTempPath(), "algeriapos-host", Guid.NewGuid().ToString("n"));
        // A host that starts the way most do starts on a copy of one already migrated and
        // bootstrapped, rather than running every migration again: about four seconds a host.
        if (root is null && services is null && StartsLikeTheTemplate(settings))
            CopyDirectory(Template(), Root);
        DatabaseDirectory = Path.Combine(Root, "database");
        var webRoot = Path.Combine(Root, "wwwroot");
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(webRoot);
        ConnectionString = "Data Source=" + Path.Combine(DatabaseDirectory, "pos.db");
        FilesRoot = Path.Combine(Root, "files");

        // The Angular build does not run in this suite, so the two files whose caching the
        // tests are about are written by hand. Only their names matter.
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><title>pos</title>");
        File.WriteAllText(Path.Combine(webRoot, "ngsw.json"), "{\"configVersion\":1}");
        File.WriteAllText(Path.Combine(webRoot, "main-TEST0001.js"), "export {};");

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_WEBROOT"] = webRoot,
            ["Database__Provider"] = "Sqlite",
            // A test host is the case the opt-in exists for; the guard itself has its own test.
            ["Database__AllowSqliteInProduction"] = "true",
            ["Database__StartupWaitSeconds"] = "0",
            ["ConnectionStrings__Default"] = ConnectionString,
            ["Jwt__Key"] = "tests-only-signing-key-0123456789abcdef",
            ["AllowedHosts"] = "*",
            ["Bootstrap__AdminUsername"] = BootstrapUsername,
            ["Bootstrap__AdminPassword"] = BootstrapPassword,
            ["DataProtection__KeysPath"] = Path.Combine(Root, "keys"),
            ["Files__Root"] = FilesRoot,
            // The outbox worker has nothing to send here; keep it out of the log.
            ["IntegrationOutbox__IntervalSeconds"] = "3600",
            // A test fires hundreds of calls a second from one address; the limits have their own
            // tests (RequestLimitsTests), which switch them back on with small values.
            ["RateLimits__Enabled"] = "false",
        };
        if (settings is not null)
            foreach (var (key, value) in settings)
            {
                if (value is null) environment.Remove(key);
                else environment[key] = value;
            }

        lock (Gate)
        {
            var previous = environment.Keys.ToDictionary(x => x, Environment.GetEnvironmentVariable);
            foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
            try
            {
                // Builds the host here and now, inside the lock, so a startup refusal is thrown
                // from the constructor — which is the shape the bootstrap tests assert.
                _ = Server;
            }
            finally
            {
                foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    public const string BootstrapUsername = "patron";
    public const string BootstrapPassword = "premier-demarrage-2026";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (services is not null) builder.ConfigureTestServices(services);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        // Closing the pooled SQLite handles is what lets the directory go on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (keepRoot) return;
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private readonly bool keepRoot;

    // ---------- the migrated template ----------

    /// <summary>
    /// Settings a host may carry and still start on the template: none of them reaches the
    /// database, the bootstrap or the seed. Anything else (a provider, a connection string, a
    /// bootstrap account, demo data) gets a database of its own, migrated from nothing.
    /// </summary>
    private static readonly string[] TemplateSafePrefixes = ["Proxy__", "RateLimits__", "Https__"];

    private static bool StartsLikeTheTemplate(IDictionary<string, string?>? settings) =>
        settings is null || settings.Keys.All(k => TemplateSafePrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)));

    private static readonly object TemplateGate = new();
    private static string? template;

    /// <summary>
    /// One host's directory, built once per test run the ordinary way — every migration, the
    /// authorization bootstrap, the first administrator — then closed and kept, so that later hosts
    /// copy it instead of migrating again. What a copy holds is exactly what a fresh start writes.
    /// </summary>
    private static string Template()
    {
        lock (TemplateGate)
        {
            if (template is not null) return template;
            var root = Path.Combine(Path.GetTempPath(), "algeriapos-host", "template-" + Guid.NewGuid().ToString("n"));
            using (new PosHost(null, root, null, keepRoot: true)) { }
            // Nothing is open any more: SQLite has folded its write-ahead log back into the file.
            // The per-host web root is written by each host itself.
            Directory.Delete(Path.Combine(root, "wwwroot"), recursive: true);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
            return template = root;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}

/// <summary>
/// One collection, so no two hosts are built at the same moment: building one reads the
/// process environment, and <see cref="PosHost"/> writes it for exactly that instant.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HostCollection
{
    public const string Name = "http host";
}
