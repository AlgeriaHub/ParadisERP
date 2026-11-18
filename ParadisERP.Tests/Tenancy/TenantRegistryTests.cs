using ParadisERP.Infrastructure.Tenancy;

namespace ParadisERP.Tests.Tenancy;

public class TenantRegistryTests
{
    private static TenantOptions Company(string id, string host, string database) => new()
    {
        Id = id, Hosts = [host], ConnectionString = "Data Source=" + database
    };

    [Fact]
    public void Unknown_hosts_have_no_default_company()
    {
        var first = Company("first", "first.example.com", "first.db");
        var registry = new TenantRegistry([first, Company("second", "second.example.com", "second.db")]);
        Assert.Same(first, registry.Resolve("FIRST.example.com"));
        Assert.Null(registry.Resolve("unknown.example.com"));
        Assert.Null(registry.Resolve("first.example.com.attacker.test"));
    }

    [Fact]
    public void Aliases_to_the_same_database_are_rejected() => Assert.Throws<ArgumentException>(() =>
        new TenantRegistry([Company("a", "a.example.com", "shared.db"), Company("b", "b.example.com", "./shared.db")]));

    [Fact]
    public void Duplicate_hostnames_are_rejected() => Assert.Throws<ArgumentException>(() =>
        new TenantRegistry([Company("a", "shop.example.com", "a.db"), Company("b", "SHOP.example.com", "b.db")]));

    [Fact]
    public void Replace_publishes_a_new_validated_set_atomically()
    {
        var first = Company("first", "first.example.com", "first.db");
        var registry = new TenantRegistry([first]);
        var second = Company("second", "second.example.com", "second.db");
        registry.Replace([first, second]);
        Assert.Same(second, registry.Resolve("second.example.com"));

        Assert.Throws<ArgumentException>(() => registry.Replace([first, Company("third", "FIRST.example.com", "third.db")]));
        Assert.Throws<ArgumentException>(() => registry.Replace([first, Company("third", "third.example.com", "./first.db")]));
        Assert.Equal(2, registry.Tenants.Count);
        Assert.Null(registry.Resolve("third.example.com"));
    }

    [Fact]
    public void Archived_companies_keep_their_hosts_and_database_but_do_not_resolve()
    {
        var archived = Company("old", "old.example.com", "old.db");
        archived.Archived = true;
        var registry = new TenantRegistry([archived]);
        Assert.Null(registry.Resolve("old.example.com"));
        Assert.Throws<ArgumentException>(() => registry.Replace([archived, Company("new", "old.example.com", "new.db")]));
        Assert.Throws<ArgumentException>(() => registry.Replace([archived, Company("new", "new.example.com", "old.db")]));
    }

    [Fact]
    public void Hosted_registry_may_start_empty_and_reserves_the_operator_host()
    {
        var registry = new TenantRegistry([], reservedHost: "ops.example.com");
        Assert.Empty(registry.Tenants);
        Assert.Throws<ArgumentException>(() => registry.Replace([Company("a", "OPS.example.com", "a.db")]));
    }

    [Fact]
    public void Single_shop_registry_cannot_be_replaced()
    {
        var registry = new TenantRegistry([new TenantOptions { Id = "default", ConnectionString = "Data Source=shop.db" }], singleCompany: true);
        Assert.Throws<InvalidOperationException>(() => registry.Replace([Company("a", "a.example.com", "a.db")]));
    }

    [Fact]
    public void Request_cannot_switch_companies()
    {
        var context = new TenantContext();
        context.Initialize(Company("a", "a.example.com", "a.db"));
        Assert.Throws<InvalidOperationException>(() => context.Initialize(Company("b", "b.example.com", "b.db")));
    }
}
