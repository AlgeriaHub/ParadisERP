using AlgeriaPos.Domain;

namespace AlgeriaPos.Tests.Stock;

/// <summary>
/// A single quantity could say how much there was; it could not say that some of it is out
/// with a customer and coming back. These cover the distinctions that difference creates.
/// </summary>
public class StockStateTests
{
    [Fact]
    public void Owned_is_everything_the_company_still_holds()
    {
        var stock = new ProductStock { StockQty = 120, OnRentQty = 380, MaintenanceQty = 25, ScrapQty = 15 };

        Assert.Equal(540, stock.OwnedQty);
        // Available is what can be sold, hired out or transferred — not what is owned.
        Assert.Equal(120, stock.StockQty);
    }

    [Fact]
    public void Written_off_stock_is_absent_rather_than_held_in_a_bucket()
    {
        // There is no WrittenOffQty on purpose. A write-off leaves; a state stays. Keeping a
        // running total of what no longer exists would put it back into the asset figure.
        var states = Enum.GetNames<StockState>();
        Assert.Contains(nameof(StockState.WrittenOff), states);
        Assert.Null(typeof(ProductStock).GetProperty("WrittenOffQty"));
    }

    [Fact]
    public void An_item_carries_four_independent_values()
    {
        var pipe = new Product { Price = 2000m, CostPrice = 1200m, AssetUnitValue = 800m, TaxRate = 19m };

        // Bought at 1200, carried at 800, charged out at 2000. None derives from another, and
        // treating any pair as the same number is how a rental business loses money.
        Assert.NotEqual(pipe.CostPrice, pipe.AssetUnitValue);
        Assert.NotEqual(pipe.Price, pipe.AssetUnitValue);
        Assert.Equal(800m, pipe.AssetUnitValue);
    }

    [Fact]
    public void A_branch_starts_in_setup_so_it_can_be_given_an_opening_balance()
    {
        // New branches accept opening balances; the migration marks every pre-existing branch
        // as trading, because it has been.
        Assert.False(new Branch().IsOpenForOperation);
    }

    [Fact]
    public void An_admin_can_see_other_branches_and_a_cashier_cannot()
    {
        var admin = new User { Role = UserRole.Admin };
        var manager = new User { Role = UserRole.Manager };
        var cashier = new User { Role = UserRole.Cashier };

        // An admin already administers every branch; withholding a read-only view of one
        // would be theatre rather than a control.
        Assert.Contains(Permissions.ViewOtherBranchStock, Permissions.For(admin));
        Assert.Empty(Permissions.For(manager));
        Assert.Empty(Permissions.For(cashier));
    }

    [Fact]
    public void A_manager_can_be_granted_the_permission_explicitly()
    {
        var manager = new User
        {
            Role = UserRole.Manager,
            Permissions = Permissions.Store([Permissions.ViewOtherBranchStock])
        };

        // This is the case a role cannot express: some branch managers need it, some do not.
        Assert.Contains(Permissions.ViewOtherBranchStock, Permissions.For(manager));
    }

    [Fact]
    public void Unknown_permission_names_are_dropped_rather_than_trusted()
    {
        var user = new User { Role = UserRole.Cashier, Permissions = "stock.view_other_branches,platform.everything" };

        // A name nobody defined must never become a grant, however it got into the column.
        Assert.Equal([Permissions.ViewOtherBranchStock], Permissions.For(user));
        Assert.Equal(Permissions.ViewOtherBranchStock, Permissions.Store(["platform.everything", Permissions.ViewOtherBranchStock]));
        Assert.Equal("", Permissions.Store(["nonsense"]));
    }

    [Fact]
    public void Granting_the_same_permission_twice_stores_it_once()
    {
        var stored = Permissions.Store([Permissions.ViewOtherBranchStock, Permissions.ViewOtherBranchStock.ToUpperInvariant()]);
        Assert.Equal(Permissions.ViewOtherBranchStock, stored);
    }
}
