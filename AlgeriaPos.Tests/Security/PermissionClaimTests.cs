using AlgeriaPos.Domain;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// <see cref="Permissions"/> turns a stored string into JWT claims, so what it emits is what an
/// authorization policy is handed. Two properties have to hold: a crafted value can never widen
/// what a user holds, and a value that <i>is</i> a real grant has to arrive spelled the way the
/// policy compares it.
/// </summary>
public class PermissionClaimTests
{
    [Fact]
    public void A_stored_value_can_never_name_a_permission_that_does_not_exist()
    {
        // Whatever is in the column — an invented name, a role, a whole list of them — only
        // members of Permissions.All survive, so widening is not expressible.
        var user = new User
        {
            Role = UserRole.Cashier,
            Permissions = "platform.everything, users.write ,*, admin, " + Permissions.ViewOtherBranchStock
        };

        Assert.Equal([Permissions.ViewOtherBranchStock], Permissions.For(user));
        Assert.Equal("", Permissions.Store(["platform.everything", "*", "", "   "]));
        Assert.Empty(Permissions.For(new User { Role = UserRole.Manager, Permissions = "platform.everything" }));
    }

    [Fact]
    public void A_grant_is_claimed_in_the_spelling_the_policy_compares()
    {
        // RequireClaim compares a claim's value with an ordinal comparer, so a row written in
        // another case would mint a claim that reads as granted in the user list and is refused
        // at the endpoint. That fails closed, which is the right direction — and silently,
        // which is not.
        var user = new User { Role = UserRole.Manager, Permissions = Permissions.ViewOtherBranchStock.ToUpperInvariant() };

        Assert.Equal([Permissions.ViewOtherBranchStock], Permissions.For(user));
        Assert.All(Permissions.For(user), granted => Assert.Contains(granted, Permissions.All, StringComparer.Ordinal));
    }

    [Fact]
    public void An_administrator_holds_it_by_role_and_nobody_else_holds_it_by_default()
    {
        Assert.Contains(Permissions.ViewOtherBranchStock, Permissions.For(new User { Role = UserRole.Admin }));
        Assert.Empty(Permissions.For(new User { Role = UserRole.Manager }));
        Assert.Empty(Permissions.For(new User { Role = UserRole.Cashier }));

        // A cashier with the grant written into the column still holds only this one read-only
        // permission — it is not a route to anything a cashier could not already reach.
        var cashier = new User { Role = UserRole.Cashier, Permissions = Permissions.Store([Permissions.ViewOtherBranchStock]) };
        Assert.Equal([Permissions.ViewOtherBranchStock], Permissions.For(cashier));
    }
}
