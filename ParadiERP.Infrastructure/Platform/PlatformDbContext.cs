using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ParadiERP.Infrastructure.Platform;

public sealed class SubscriptionOverride
{
    public string CompanyId { get; set; } = "";
    public string SettingsJson { get; set; } = "";
    public long Revision { get; set; }
}

/// <summary>Operator-edited country profile for one company; wins over configuration.</summary>
public sealed class RegionalOverride
{
    public string CompanyId { get; set; } = "";
    public string SettingsJson { get; set; } = "";
    public long Revision { get; set; }
}

/// <summary>An operator-uploaded UI translation, shared by every company that enables it.</summary>
public sealed class LanguagePackRow
{
    public string Code { get; set; } = "";
    public string PackJson { get; set; } = "";
    public long Revision { get; set; }
}

/// <summary>
/// A company the operator created in the portal. Its connection string is encrypted with ASP.NET
/// Data Protection, so the key ring must persist (see DataProtection:KeysPath).
/// </summary>
public sealed class ProvisionedCompany
{
    public string Id { get; set; } = "";
    public string HostsJson { get; set; } = "[]";
    public string DatabaseProvider { get; set; } = "";
    public string ProtectedConnectionString { get; set; } = "";
    public string AdminUsername { get; set; } = "";
    public string Country { get; set; } = "";
    /// <summary>Active or Archived.</summary>
    public string State { get; set; } = "Active";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public long Revision { get; set; }
}

/// <summary>Operator-managed integrations of one company. Secrets are Data Protection-encrypted.</summary>
public sealed class CompanyIntegrationsRow
{
    public string CompanyId { get; set; } = "";
    public string SettingsJson { get; set; } = "";
    public string ProtectedSecretsJson { get; set; } = "";
    public long Revision { get; set; }
}

public sealed class PlatformAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CompanyId { get; set; } = "";
    public string ActorSubject { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ExternalIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CompanyId { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public Guid UserId { get; set; }
}

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<SubscriptionOverride> Subscriptions => Set<SubscriptionOverride>();
    public DbSet<RegionalOverride> Regional => Set<RegionalOverride>();
    public DbSet<LanguagePackRow> LanguagePacks => Set<LanguagePackRow>();
    public DbSet<ProvisionedCompany> Companies => Set<ProvisionedCompany>();
    public DbSet<CompanyIntegrationsRow> Integrations => Set<CompanyIntegrationsRow>();
    public DbSet<PlatformAudit> Audit => Set<PlatformAudit>();
    public DbSet<ExternalIdentity> Identities => Set<ExternalIdentity>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SubscriptionOverride>(e =>
        {
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyId).HasMaxLength(100);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<RegionalOverride>(e =>
        {
            e.ToTable("RegionalOverrides");
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyId).HasMaxLength(100);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<LanguagePackRow>(e =>
        {
            e.ToTable("LanguagePacks");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ProvisionedCompany>(e =>
        {
            e.ToTable("ProvisionedCompanies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.DatabaseProvider).HasMaxLength(20);
            e.Property(x => x.AdminUsername).HasMaxLength(64);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<CompanyIntegrationsRow>(e =>
        {
            e.ToTable("CompanyIntegrations");
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyId).HasMaxLength(100);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<PlatformAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CompanyId, x.CreatedAtUtc });
        });
        model.Entity<ExternalIdentity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CompanyId).HasMaxLength(100);
            e.Property(x => x.Issuer).HasMaxLength(400);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.HasIndex(x => new { x.CompanyId, x.Issuer, x.Subject }).IsUnique();
        });

        // The portal's times read back as UTC too, so its audit list shows the hour things happened.
        UtcDateTimes.Apply(model);
    }
}

public sealed class PlatformDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=platform-design.db").Options);
}
