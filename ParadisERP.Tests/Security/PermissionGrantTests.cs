using ParadisERP.Domain;

namespace ParadisERP.Tests.Security;

/// <summary>
/// Granting a named permission, which until now could be enforced but not given.
///
/// <para>
/// The security review found the gap: <c>ViewOtherBranchStock</c> was defined, required at the
/// endpoint and issued as a claim, and no request carried it — so the only holder it could ever
/// have was an administrator, through the role fallback. The Oran branch manager the design
/// describes could not be given it. That is the same shape of defect as a column nothing
/// writes: everything compiles, and the feature cannot be reached.
/// </para>
/// </summary>
public class PermissionGrantTests
{
    [Fact]
    public void A_manager_holds_only_what_was_granted_to_them()
    {
        var plain = new User { Role = UserRole.Manager };
        Assert.Empty(Permissions.For(plain));

        var trusted = new User
        {
            Role = UserRole.Manager,
            Permissions = Permissions.Store([Permissions.ViewOtherBranchStock])
        };
        Assert.Contains(Permissions.ViewOtherBranchStock, Permissions.For(trusted));
    }

    [Fact]
    public void An_administrator_holds_it_by_the_role_and_not_by_a_grant()
    {
        var admin = new User { Role = UserRole.Admin };

        // Nothing was granted, and they hold it anyway — an admin already administers every
        // branch, so withholding a read-only view of one would be theatre. The distinction
        // matters to a screen: this is inherited, not something it may revoke.
        Assert.Empty(Permissions.Parse(admin.Permissions));
        Assert.Contains(Permissions.ViewOtherBranchStock, Permissions.For(admin));
    }

    [Fact]
    public void A_typo_grants_nothing_rather_than_sitting_in_the_column_looking_granted()
    {
        // Stored, so it is normalised on the way in rather than at every read.
        Assert.Equal("", Permissions.Store(["stock.view_other_branch"]));
        Assert.Equal("", Permissions.Store(["platform.everything"]));
        Assert.Equal("", Permissions.Store([""]));
        Assert.Equal("", Permissions.Store(null));
    }

    [Fact]
    public void A_grant_is_stored_in_the_spelling_the_policy_compares()
    {
        // The claim value is compared with an ordinal comparer, so a row written in another
        // case would read as granted in a user list and be refused at the endpoint — failing
        // closed, which is the right direction, and silently, which is not.
        var shouted = new User
        {
            Role = UserRole.Cashier,
            Permissions = Permissions.ViewOtherBranchStock.ToUpperInvariant()
        };

        Assert.Equal([Permissions.ViewOtherBranchStock], Permissions.For(shouted));
    }

    [Fact]
    public void Granting_the_same_thing_twice_stores_it_once()
    {
        var stored = Permissions.Store(
            [Permissions.ViewOtherBranchStock, Permissions.ViewOtherBranchStock.ToUpperInvariant()]);

        Assert.Equal(Permissions.ViewOtherBranchStock, stored);
    }

    [Fact]
    public void Every_named_permission_can_actually_be_granted()
    {
        // The guard against this whole class of defect: a permission added later must be
        // grantable through the same path, or it repeats exactly the gap this closes.
        foreach (var permission in Permissions.All)
        {
            Assert.Equal(permission, Permissions.Store([permission]));
            var user = new User { Role = UserRole.Cashier, Permissions = Permissions.Store([permission]) };
            Assert.Contains(permission, Permissions.For(user));
        }
    }
}
