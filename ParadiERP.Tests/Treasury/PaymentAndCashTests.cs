using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Seeding;
using ParadiERP.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Treasury;

/// <summary>
/// Payments stopped being a closed list of five names and became definitions with a state:
/// what a tender does, and whether the money behind it has actually arrived. These cover the
/// parts where getting it wrong costs money rather than just looking untidy.
/// </summary>
public class PaymentAndCashTests
{
    private static Dictionary<string, PaymentMethodDef> Defs(params PaymentMethodDef[] extra)
    {
        var defs = new Dictionary<string, PaymentMethodDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["cash"] = new() { Key = "cash", Behavior = PaymentBehavior.Cash, RequiresConfirmation = false, AffectsCashDrawer = true },
            ["credit"] = new() { Key = "credit", Behavior = PaymentBehavior.Credit, RequiresConfirmation = false }
        };
        foreach (var def in extra) defs[def.Key] = def;
        return defs;
    }

    private static SalePaymentPush Push(string method, decimal amount = 100, string? key = null) =>
        new(Guid.NewGuid(), method, amount, null, key);

    [Fact]
    public void Cash_is_confirmed_on_the_spot()
    {
        var warnings = new List<string>();
        var payment = SyncService.BuildPayment(Guid.NewGuid(), Push("cash"), Defs(), warnings);

        Assert.Equal(PaymentStatus.Confirmed, payment.Status);
        Assert.Equal(PaymentBehavior.Cash, payment.Behavior);
        Assert.NotNull(payment.ConfirmedAtUtc);
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_tender_that_needs_confirming_starts_pending()
    {
        var cheque = new PaymentMethodDef
        {
            Key = "cheque", Behavior = PaymentBehavior.Generic,
            RequiresConfirmation = true, RequiresInstrumentDetails = true
        };
        var due = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var push = new SalePaymentPush(Guid.NewGuid(), "cheque", 500, null, "cheque", "C-4471", "BNA", due);

        var payment = SyncService.BuildPayment(Guid.NewGuid(), push, Defs(cheque), []);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.ConfirmedAtUtc);
        Assert.Equal("C-4471", payment.InstrumentNumber);
        Assert.Equal("BNA", payment.InstrumentBank);
        Assert.Equal(due, payment.InstrumentDueDate);
    }

    [Fact]
    public void An_operator_defined_tender_travels_by_key_and_is_generic()
    {
        var postal = new PaymentMethodDef { Key = "postal-transfer", Behavior = PaymentBehavior.Generic, RequiresConfirmation = true };
        var payment = SyncService.BuildPayment(Guid.NewGuid(), Push("postal-transfer", 750, "postal-transfer"), Defs(postal), []);

        Assert.Equal("postal-transfer", payment.MethodKey);
        Assert.Equal(PaymentBehavior.Generic, payment.Behavior);
        // Nothing in the built-in enum describes it, so the legacy column says "see the lines".
        Assert.Equal(PaymentMethod.Mixed, payment.Method);
    }

    [Fact]
    public void An_unknown_tender_is_recorded_with_a_warning_and_treated_as_unpaid()
    {
        var warnings = new List<string>();
        var payment = SyncService.BuildPayment(Guid.NewGuid(), Push("mystery-voucher"), Defs(), warnings);

        // Never rejected: the customer has already paid and left, and refusing the record
        // would only lose the sale. But an unrecognised tender cannot be vouched for either.
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Contains(warnings, w => w.Contains("mystery-voucher"));
    }

    [Fact]
    public void A_till_that_only_sends_the_legacy_name_still_resolves()
    {
        var payment = SyncService.BuildPayment(Guid.NewGuid(), Push("Credit"), Defs(), []);

        Assert.Equal("Credit", payment.MethodKey);
        Assert.Equal(PaymentBehavior.Credit, payment.Behavior);
        Assert.Equal(PaymentMethod.Credit, payment.Method);
    }

    [Fact]
    public async Task Built_in_tenders_and_a_cash_box_per_branch_are_created_and_not_duplicated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(
            new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Branches.Add(new Branch { NameFr = "Dépôt central", IsMain = true });
        db.Branches.Add(new Branch { NameFr = "Oran" });
        await db.SaveChangesAsync();

        await CoreDataSeeder.EnsureAsync(db);
        await CoreDataSeeder.EnsureAsync(db); // an upgrade runs this on every startup

        var methods = await db.PaymentMethodDefs.ToListAsync();
        Assert.Equal(PaymentMethodDef.BuiltInKeys.Length, methods.Count);
        Assert.All(methods, m => Assert.True(m.IsBuiltIn));
        Assert.All(methods, m => Assert.True(m.Version > 0));

        // Only physical cash reaches the drawer, or the Z-report shows a surplus on every
        // card sale and a shortfall on every cheque.
        Assert.Single(methods.Where(m => m.AffectsCashDrawer));
        Assert.Equal("cash", methods.Single(m => m.AffectsCashDrawer).Key);
        Assert.False(methods.Single(m => m.Key == "cash").RequiresConfirmation);

        var boxes = await db.CashBoxes.ToListAsync();
        Assert.Equal(2, boxes.Count);
        Assert.Single(boxes.Where(b => b.IsMain));
    }

    [Fact]
    public async Task A_renamed_or_disabled_tender_is_left_alone_on_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(
            new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        await db.SaveChangesAsync();

        await CoreDataSeeder.EnsureAsync(db);
        var card = await db.PaymentMethodDefs.SingleAsync(m => m.Key == "card");
        card.NameFr = "TPE Banque";
        card.IsActive = false;
        await db.SaveChangesAsync();

        await CoreDataSeeder.EnsureAsync(db);

        // Restoring these would silently undo a deliberate decision, which is worse than
        // the gap the repair was meant to fill.
        var after = await db.PaymentMethodDefs.SingleAsync(m => m.Key == "card");
        Assert.Equal("TPE Banque", after.NameFr);
        Assert.False(after.IsActive);
    }

    [Fact]
    public void Country_profiles_accept_an_operator_defined_tender_but_still_demand_cash()
    {
        var settings = RegionalPresets.Create("DZ")!;
        settings.PaymentMethods = ["Cash", "Credit", "Virement CCP"];
        RegionalSettingsValidator.NormalizeAndValidate(settings);
        Assert.Contains("Virement CCP", settings.PaymentMethods);

        // A till that cannot take cash cannot trade: it is the only tender needing no
        // device, no network and no confirmation.
        var withoutCash = RegionalPresets.Create("DZ")!;
        withoutCash.PaymentMethods = ["Card", "Credit"];
        Assert.Throws<ArgumentException>(() => RegionalSettingsValidator.NormalizeAndValidate(withoutCash));

        var empty = RegionalPresets.Create("DZ")!;
        empty.PaymentMethods = [];
        Assert.Throws<ArgumentException>(() => RegionalSettingsValidator.NormalizeAndValidate(empty));
    }

    [Fact]
    public void A_sale_can_be_raised_without_tax_when_the_customer_does_not_need_it()
    {
        Assert.True(Enum.TryParse<TaxMode>("Untaxed", true, out var untaxed));
        Assert.Equal(TaxMode.Untaxed, untaxed);
        // Absent on the wire means taxed, so a till that has not been updated keeps
        // producing exactly what it produced before rather than silently dropping tax.
        Assert.False(Enum.TryParse<TaxMode>(null, true, out _));
        Assert.Equal(TaxMode.Taxed, new Sale().TaxMode);
    }
}
