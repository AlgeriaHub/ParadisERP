using System.Security.Claims;
using ParadiERP.API.Endpoints;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Security;

/// <summary>
/// Owner rule (docs/owner-notes.md, 2026-10-11, answer 4): a cheque taken at the till needs a
/// second person to confirm it — the same rule and the same permission
/// <see cref="PermissionCatalog.ReceiptsSettleOwn"/> as back-office receipts.
/// Till sales are never refused: the sale is recorded and its cheque tender stays Pending;
/// only the confirmation in the back office checks who confirms.
/// A bounce is never refused (it takes nothing off any debt).
/// </summary>
public class TillChequeSettlementSecurityTests
{
    private static async Task<(Sale Sale, SalePayment Payment)> AddChequeSaleAsync(
        PosDbContext db, Guid cashierUserId, string reference = "C01-000100")
    {
        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            Reference = reference,
            LocalSeq = 1,
            SoldAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            BranchId = TillHarness.Algiers,
            CashierUserId = cashierUserId,
            CashierName = "Cashier Test",
            SubTotalHt = 5000m,
            GrandTotalTtc = 5000m,
            PaidAmount = 5000m,
            Status = SaleStatus.Completed,
            PaymentMethod = PaymentMethod.Card
        };
        var payment = new SalePayment
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            Method = PaymentMethod.Card,
            MethodKey = "cheque",
            Amount = 5000m,
            Status = PaymentStatus.Pending,
            InstrumentNumber = "CHQ-0099",
            InstrumentBank = "BNA"
        };
        db.Sales.Add(sale);
        db.SalePayments.Add(payment);
        await db.SaveChangesAsync();
        return (sale, payment);
    }

    [Fact]
    public async Task Confirming_own_cheque_without_permission_returns_409_and_stays_pending()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (cashier, cashierPrincipal) = await BranchLockTests.UserAsync(db, UserRole.Cashier, TillHarness.Algiers);
        var (sale, payment) = await AddChequeSaleAsync(db, cashier.Id);

        var (status, body) = await BranchLockTests.InvokeAsync(
            db,
            app => app.MapTreasuryEndpoints(),
            "POST",
            "/api/treasury/pending-payments/{id:guid}/settle",
            cashierPrincipal,
            """{"status":"Confirmed","note":null}""",
            ("id", payment.Id.ToString()));

        Assert.Equal(409, status);
        Assert.Contains("confirm_own_forbidden", body);
        Assert.Contains(sale.Reference, body);

        var reloaded = await db.SalePayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Pending, reloaded.Status);
        Assert.Null(reloaded.ConfirmedAtUtc);
    }

    [Fact]
    public async Task Confirming_cheque_as_another_user_is_confirmed()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (cashier, _) = await BranchLockTests.UserAsync(db, UserRole.Cashier, TillHarness.Algiers);
        var (_, otherPrincipal) = await BranchLockTests.UserAsync(db, UserRole.Manager, TillHarness.Algiers);
        var (_, payment) = await AddChequeSaleAsync(db, cashier.Id);

        var (status, _) = await BranchLockTests.InvokeAsync(
            db,
            app => app.MapTreasuryEndpoints(),
            "POST",
            "/api/treasury/pending-payments/{id:guid}/settle",
            otherPrincipal,
            """{"status":"Confirmed","note":null}""",
            ("id", payment.Id.ToString()));

        Assert.Equal(200, status);

        var reloaded = await db.SalePayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Confirmed, reloaded.Status);
        Assert.NotNull(reloaded.ConfirmedAtUtc);
    }

    [Fact]
    public async Task Confirming_own_cheque_with_settle_own_permission_is_confirmed()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (cashier, cashierPrincipal) = await BranchLockTests.UserAsync(db, UserRole.Cashier, TillHarness.Algiers);
        var cashierWithGrant = TillHarness.WithGrant(cashierPrincipal, PermissionCatalog.ReceiptsSettleOwn);
        var (_, payment) = await AddChequeSaleAsync(db, cashier.Id);

        var (status, _) = await BranchLockTests.InvokeAsync(
            db,
            app => app.MapTreasuryEndpoints(),
            "POST",
            "/api/treasury/pending-payments/{id:guid}/settle",
            cashierWithGrant,
            """{"status":"Confirmed","note":null}""",
            ("id", payment.Id.ToString()));

        Assert.Equal(200, status);

        var reloaded = await db.SalePayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Confirmed, reloaded.Status);
        Assert.NotNull(reloaded.ConfirmedAtUtc);
    }

    [Fact]
    public async Task Bouncing_own_cheque_as_same_user_is_allowed()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (cashier, cashierPrincipal) = await BranchLockTests.UserAsync(db, UserRole.Cashier, TillHarness.Algiers);
        var (_, payment) = await AddChequeSaleAsync(db, cashier.Id);

        var (status, _) = await BranchLockTests.InvokeAsync(
            db,
            app => app.MapTreasuryEndpoints(),
            "POST",
            "/api/treasury/pending-payments/{id:guid}/settle",
            cashierPrincipal,
            """{"status":"Bounced","note":"Sans provision"}""",
            ("id", payment.Id.ToString()));

        Assert.Equal(200, status);

        var reloaded = await db.SalePayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Bounced, reloaded.Status);
        Assert.Equal("Sans provision", reloaded.StatusNote);
        Assert.NotNull(reloaded.ConfirmedAtUtc);
    }
}
