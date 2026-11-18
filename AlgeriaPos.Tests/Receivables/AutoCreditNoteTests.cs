using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Receivables;

/// <summary>
/// Automatic credit notes for partial refunds of invoiced movements (owner rules 1–4, 2026-10-10).
/// A till refund of an invoiced sale raises its credit note automatically so tax documents reflect
/// the refund without posting twice to the ledger or moving customer balance a second time.
/// </summary>
public class AutoCreditNoteTests
{
    private static SalePush RefundPush(Guid saleId, Guid customerId, decimal amount, decimal qty, string reference = "T-R0001") =>
        new(
            Id: Guid.NewGuid(),
            Reference: reference,
            LocalSeq: 2,
            SoldAtUtc: DateTime.UtcNow,
            CustomerId: customerId,
            CashierUserId: Guid.NewGuid(),
            CashierName: "Cashier",
            SubTotalHt: amount,
            TaxTotal: 0m,
            DiscountTotal: 0m,
            GrandTotalTtc: amount,
            PaidAmount: amount,
            ChangeAmount: 0m,
            PaymentMethod: nameof(PaymentMethod.Cash),
            Status: nameof(SaleStatus.Refunded),
            RefundOfSaleId: saleId,
            Note: null,
            Lines: [new SaleLinePush(Guid.NewGuid(), ReceivableHarness.Concrete, "BET-25", "Béton C25/30", qty, amount / qty, 0m, 0m, amount, 0m)],
            Payments: [Security.TillHarness.Tender("cash", amount)],
            RoundingAdjustment: 0m,
            TaxMode: null);

    [Fact]
    public async Task Till_refund_of_invoiced_sale_raises_automatic_credit_note_without_double_posting()
    {
        await using var harness = await ReceivableHarness.NewAsync();
        var customerId = ReceivableHarness.Contractor;

        // 1. Invoice a credit sale
        var saleId = await harness.PushSaleAsync(total: 10_000m, cash: 0m, customerId: customerId);
        var invoiceRes = await harness.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(
                DueDateUtc: DateTime.UtcNow.AddDays(30),
                SaleId: saleId),
            by: "admin");
        Assert.True(invoiceRes.Ok);
        var invoice = invoiceRes.Value!;
        Assert.Equal(10_000m, await harness.OutstandingAsync(invoice.Id));

        // 2. Push a refund of part of it through SyncService (the way SyncRefundTests builds refunds)
        var refundAmount = 4_000m;
        var refundQty = 0.4m;
        var refund = RefundPush(saleId, customerId, refundAmount, refundQty);

        var sync = Security.TillHarness.NewService(harness.Db);
        var pushRes = await sync.PushAsync(new PushRequest("C01", [refund]), "C01", CancellationToken.None);
        Assert.True(pushRes.Accepted == 1, string.Join(" | ", pushRes.Results.Select(x => x.Code + ": " + x.Message)));

        var balanceAfterRefundPush = await harness.BalanceAsync(customerId);
        var creditNoteEntriesBefore = await harness.Db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.CreditNote);

        // 3. Call CreditTillRefundAsync
        var creditRes = await harness.Receivables.CreditTillRefundAsync(refund.Id, "admin", CancellationToken.None);
        Assert.NotNull(creditRes);
        Assert.True(creditRes.Ok);
        var note = creditRes.Value!;

        // One credit note with SourceType "sale" and SourceId = refund.Id
        Assert.Equal("sale", note.SourceType);
        Assert.Equal(refund.Id, note.SourceId);
        Assert.Equal(invoice.Id, note.InvoiceId);

        // Invoice outstanding lowered by the refunded amount
        var outstandingAfter = await harness.OutstandingAsync(invoice.Id);
        Assert.Equal(10_000m - refundAmount, outstandingAfter);

        // NO new ledger entry with SourceType credit-note (the refund already posted)
        var creditNoteEntriesAfter = await harness.Db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.CreditNote);
        Assert.Equal(creditNoteEntriesBefore, creditNoteEntriesAfter);

        // Customer balance unchanged by the note (the refund already moved it)
        var balanceAfterCreditNote = await harness.BalanceAsync(customerId);
        Assert.Equal(balanceAfterRefundPush, balanceAfterCreditNote);
    }

    [Fact]
    public async Task Calling_CreditTillRefundAsync_twice_creates_one_note_only()
    {
        await using var harness = await ReceivableHarness.NewAsync();
        var customerId = ReceivableHarness.Contractor;

        var saleId = await harness.PushSaleAsync(total: 10_000m, cash: 0m, customerId: customerId);
        var invoiceRes = await harness.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DueDateUtc: DateTime.UtcNow.AddDays(30), SaleId: saleId), by: "admin");
        Assert.True(invoiceRes.Ok);

        var refundAmount = 3_000m;
        var refund = RefundPush(saleId, customerId, refundAmount, 0.3m, "T-R0002");

        var sync = Security.TillHarness.NewService(harness.Db);
        var pushRes = await sync.PushAsync(new PushRequest("C01", [refund]), "C01", CancellationToken.None);
        Assert.True(pushRes.Accepted == 1, string.Join(" | ", pushRes.Results.Select(x => x.Code + ": " + x.Message)));

        var firstCall = await harness.Receivables.CreditTillRefundAsync(refund.Id, "admin", CancellationToken.None);
        Assert.NotNull(firstCall);
        Assert.True(firstCall.Ok);

        var secondCall = await harness.Receivables.CreditTillRefundAsync(refund.Id, "admin", CancellationToken.None);
        Assert.NotNull(secondCall);
        Assert.True(secondCall.Ok);
        Assert.Equal(firstCall.Value!.Id, secondCall.Value!.Id);

        var notesCount = await harness.Db.CreditNotes.CountAsync(n => n.SourceType == "sale" && n.SourceId == refund.Id);
        Assert.Equal(1, notesCount);
    }

    [Fact]
    public async Task Refund_of_sale_that_was_never_invoiced_returns_null()
    {
        await using var harness = await ReceivableHarness.NewAsync();
        var customerId = ReceivableHarness.Contractor;

        // Push sale, but never invoice it
        var saleId = await harness.PushSaleAsync(total: 10_000m, cash: 0m, customerId: customerId);

        var refundAmount = 2_000m;
        var refund = RefundPush(saleId, customerId, refundAmount, 0.2m, "T-R0003");

        var sync = Security.TillHarness.NewService(harness.Db);
        var pushRes = await sync.PushAsync(new PushRequest("C01", [refund]), "C01", CancellationToken.None);
        Assert.True(pushRes.Accepted == 1, string.Join(" | ", pushRes.Results.Select(x => x.Code + ": " + x.Message)));

        var result = await harness.Receivables.CreditTillRefundAsync(refund.Id, "admin", CancellationToken.None);
        Assert.Null(result);

        var notesCount = await harness.Db.CreditNotes.CountAsync(n => n.SourceType == "sale" && n.SourceId == refund.Id);
        Assert.Equal(0, notesCount);
    }
}
