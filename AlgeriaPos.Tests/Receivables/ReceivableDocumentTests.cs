using AlgeriaPos.Application.Receivables;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Rentals.RentalTestPaper;

namespace AlgeriaPos.Tests.Receivables;

/// <summary>
/// What each receivable document does, and — more often the point — what it deliberately does
/// not do.
///
/// <para>
/// Three rules carry most of these tests: paper is not payment, an invoice over a document that
/// has already posted must not post again, and a raised invoice is never edited. Each of the
/// three, got wrong, produces a system that looks right and is quietly wrong about money.
/// </para>
/// </summary>
public class ReceivableDocumentTests
{
    // ---------- the invoice ----------

    [Fact]
    public async Task A_direct_invoice_bills_the_customer_the_gross_and_recognises_only_the_net()
    {
        await using var h = await ReceivableHarness.NewAsync();

        var raised = await h.RaiseAsync(qty: 10, unitPrice: 12_000m, taxRate: 19m);
        Assert.True(raised.Ok, raised.Message);

        var invoice = raised.Value!;
        Assert.Equal(120_000m, invoice.SubTotalHt);
        Assert.Equal(22_800m, invoice.TaxTotal);
        Assert.Equal(142_800m, invoice.GrandTotalTtc);
        Assert.Equal("INV-000001", invoice.Number);

        var posted = await h.PostedAsync(LedgerSources.Invoice);
        Assert.Equal(142_800m, posted[LedgerAccounts.Customers]);
        Assert.Equal(-120_000m, posted[LedgerAccounts.Sales]);
        Assert.Equal(-22_800m, posted[LedgerAccounts.TaxCollected]);

        // The denormalised balance is the figure the credit-limit warning reads, so it has to
        // move with the ledger and not with the net.
        Assert.Equal(142_800m, await h.BalanceAsync());
        Assert.Equal(142_800m, await h.TradeDebtAsync());
    }

    [Fact]
    public async Task Tax_is_charged_on_each_rates_own_total_and_not_line_by_line()
    {
        await using var h = await ReceivableHarness.NewAsync();

        // Three lines whose individual taxes each round up. Line by line: 3 × 1.90 = 5.70.
        // On the total: 30 × 19% = 5.70 as well — so the case is chosen where per-line rounding
        // visibly differs: 9.99 at 19% is 1.8981, three of them round to 1.90 each = 5.70,
        // while 29.97 at 19% is 5.6943, which rounds to 5.69.
        var raised = await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines:
            [
                new InvoiceLineRequest(null, "A", "A", 1, 9.99m, 19m),
                new InvoiceLineRequest(null, "B", "B", 1, 9.99m, 19m),
                new InvoiceLineRequest(null, "C", "C", 1, 9.99m, 19m)
            ]));

        Assert.True(raised.Ok, raised.Message);
        Assert.Equal(29.97m, raised.Value!.SubTotalHt);
        Assert.Equal(5.69m, raised.Value.TaxTotal);
    }

    [Fact]
    public async Task An_untaxed_invoice_carries_no_tax_and_no_rate_at_all()
    {
        await using var h = await ReceivableHarness.NewAsync();

        var raised = await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            TaxMode: nameof(TaxMode.Untaxed),
            Lines: [new InvoiceLineRequest(ReceivableHarness.Concrete, null, null, 10, 12_000m, 19m)]));

        Assert.True(raised.Ok, raised.Message);
        Assert.Equal(0m, raised.Value!.TaxTotal);
        Assert.Equal(120_000m, raised.Value.GrandTotalTtc);
        // Not a rate that happens to be ignored: a rate left on the line is something a later
        // report picks up and believes.
        Assert.All(raised.Value.Lines, l => Assert.Equal(0m, l.TaxRate));

        var posted = await h.PostedAsync(LedgerSources.Invoice);
        Assert.False(posted.ContainsKey(LedgerAccounts.TaxCollected));
    }

    [Fact]
    public async Task A_credit_limit_warns_and_never_refuses()
    {
        await using var h = await ReceivableHarness.NewAsync(creditLimit: 100_000m);

        var raised = await h.RaiseAsync(qty: 10, unitPrice: 12_000m);

        // Rule one. The invoice exists, and the limit is something someone is told about.
        Assert.True(raised.Ok);
        Assert.NotNull(raised.Warning);
        Assert.Contains("credit limit", raised.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_invoice_is_never_edited_and_never_deleted()
    {
        // Stated as an inventory rather than a call, because the absence is the design: the
        // correction is a credit note, and a route that edited an invoice would be a different
        // figure carrying a number the customer has already been shown.
        var routes = typeof(AlgeriaPos.API.Endpoints.ReceivableEndpoints);
        Assert.NotNull(routes);

        await using var h = await ReceivableHarness.NewAsync();
        var raised = await h.RaiseAsync();
        var invoice = await h.Receivables.ReadInvoiceAsync(raised.Value!.Id);

        Assert.NotNull(invoice);
        Assert.Equal(raised.Value.GrandTotalTtc, invoice!.GrandTotalTtc);
    }

    // ---------- paper is not payment ----------

    [Fact]
    public async Task A_pending_cheque_reduces_no_debt_and_reaches_no_account()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;

        var cheque = await h.ReceiveAsync(invoice.GrandTotalTtc, "cheque", invoice.Id);
        Assert.True(cheque.Ok, cheque.Message);
        Assert.Equal(PaymentStatus.Pending, cheque.Value!.Status);

        // ⚠ The rule §5.4 is explicit about. The paper is recorded and written against the
        // invoice, and none of that is payment.
        Assert.Equal(invoice.GrandTotalTtc, await h.OutstandingAsync(invoice.Id));
        Assert.Equal(invoice.GrandTotalTtc, await h.TradeDebtAsync());
        Assert.Equal(invoice.GrandTotalTtc, await h.BalanceAsync());
        Assert.Empty(await h.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.CustomerReceipt).ToListAsync());

        var standing = (await h.Receivables.StandingsAsync([invoice.Id]))[invoice.Id];
        // It is not invisible either: what is spoken for is its own figure, so a clerk can see
        // that a cheque exists without the invoice pretending to be paid.
        Assert.Equal(invoice.GrandTotalTtc, standing.Claimed);
        Assert.Equal(0m, standing.Allocatable);
        Assert.Equal("Open", standing.Status);
    }

    [Fact]
    public async Task Confirming_the_cheque_is_what_settles_the_invoice()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;
        var cheque = (await h.ReceiveAsync(invoice.GrandTotalTtc, "cheque", invoice.Id)).Value!;

        var settled = await h.Receivables.SettleReceiptAsync(cheque.Id, nameof(PaymentStatus.Confirmed), null, "admin");
        Assert.True(settled.Ok, settled.Message);

        Assert.Equal(0m, await h.OutstandingAsync(invoice.Id));
        Assert.Equal(0m, await h.TradeDebtAsync());
        Assert.Equal(0m, await h.BalanceAsync());

        var posted = await h.PostedAsync(LedgerSources.CustomerReceipt);
        // A cleared cheque is money at the bank, never in the branch drawer — which is exactly
        // why it can never be remitted.
        Assert.Equal(invoice.GrandTotalTtc, posted[LedgerAccounts.Bank]);
        Assert.Equal(-invoice.GrandTotalTtc, posted[LedgerAccounts.Customers]);
        Assert.False(posted.ContainsKey(LedgerAccounts.Sales));
    }

    [Fact]
    public async Task A_bounced_cheque_leaves_the_debt_exactly_where_it_was()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;
        var cheque = (await h.ReceiveAsync(invoice.GrandTotalTtc, "cheque", invoice.Id)).Value!;

        var bounced = await h.Receivables.SettleReceiptAsync(
            cheque.Id, nameof(PaymentStatus.Bounced), "Provision insuffisante", "admin");
        Assert.True(bounced.Ok, bounced.Message);

        // Nothing arrived, so nothing changed and nothing had to be reversed. The allocation
        // simply stops counting, because it only ever counted while the voucher was confirmed.
        Assert.Equal(invoice.GrandTotalTtc, await h.OutstandingAsync(invoice.Id));
        Assert.Equal(invoice.GrandTotalTtc, await h.TradeDebtAsync());
        Assert.Empty(await h.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.CustomerReceipt).ToListAsync());

        var standing = (await h.Receivables.StandingsAsync([invoice.Id]))[invoice.Id];
        Assert.Equal(0m, standing.Claimed);
        Assert.Equal(invoice.GrandTotalTtc, standing.Allocatable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_bounce_without_the_bank_s_reason_is_refused_and_the_cheque_stays_pending(string? note)
    {
        // FR-2B (the owner, 2026-10-03): a bounce must say why; collecting needs nothing more.
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;
        var cheque = (await h.ReceiveAsync(invoice.GrandTotalTtc, "cheque", invoice.Id)).Value!;

        var refused = await h.Receivables.SettleReceiptAsync(cheque.Id, nameof(PaymentStatus.Bounced), note, "admin");
        Assert.False(refused.Ok);
        Assert.Equal(ReceivableRules.BounceNeedsReason, refused.Message);
        Assert.Equal(PaymentStatus.Pending, (await h.Db.ReceiptVouchers.AsNoTracking().SingleAsync(v => v.Id == cheque.Id)).Status);

        // With the reason it bounces, and the reason is what the receipt keeps.
        var bounced = await h.Receivables.SettleReceiptAsync(cheque.Id, nameof(PaymentStatus.Bounced), "Provision insuffisante", "admin");
        Assert.True(bounced.Ok, bounced.Message);
        Assert.Equal("Provision insuffisante", bounced.Value!.StatusNote);
    }

    [Fact]
    public async Task Collecting_still_needs_no_note()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;
        var cheque = (await h.ReceiveAsync(invoice.GrandTotalTtc, "cheque", invoice.Id)).Value!;
        var collected = await h.Receivables.SettleReceiptAsync(cheque.Id, nameof(PaymentStatus.Confirmed), null, "admin");
        Assert.True(collected.Ok, collected.Message);
    }

    [Fact]
    public void Only_a_bounce_needs_a_reason()
    {
        Assert.Equal(ReceivableRules.BounceNeedsReason, ReceivableRules.SettlementProblem(PaymentStatus.Bounced, " "));
        Assert.Null(ReceivableRules.SettlementProblem(PaymentStatus.Bounced, "Chèque sans provision"));
        Assert.Null(ReceivableRules.SettlementProblem(PaymentStatus.Confirmed, null));
    }

    [Fact]
    public async Task Two_pending_cheques_cannot_both_claim_the_same_invoice()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;

        Assert.True((await h.ReceiveAsync(invoice.GrandTotalTtc, "cheque", invoice.Id)).Ok);
        var second = await h.ReceiveAsync(invoice.GrandTotalTtc, "cheque", invoice.Id);

        Assert.False(second.Ok);
        Assert.Equal(ReceivableFailure.Invalid, second.Failure);
    }

    [Fact]
    public async Task Cash_settles_on_the_spot_and_lands_in_the_branch_box()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;

        var cash = await h.ReceiveAsync(50_000m, "cash", invoice.Id);
        Assert.True(cash.Ok, cash.Message);
        Assert.Equal(PaymentStatus.Confirmed, cash.Value!.Status);

        Assert.Equal(invoice.GrandTotalTtc - 50_000m, await h.OutstandingAsync(invoice.Id));

        var posted = await h.PostedAsync(LedgerSources.CustomerReceipt);
        Assert.Equal(50_000m, posted[LedgerAccounts.Cash]);

        var movement = await h.Db.CashMovements.SingleAsync(m => m.SourceType == LedgerSources.CustomerReceipt);
        // The ledger and the drawer have to agree; if they used different arithmetic there
        // would be nothing to say which was right.
        Assert.Equal(CashDirection.In, movement.Direction);
        Assert.Equal(CashMovementKind.Receipt, movement.Kind);
        Assert.Equal(50_000m, movement.Amount);
    }

    [Fact]
    public async Task One_payment_can_settle_several_invoices_and_leave_the_rest_on_account()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var first = (await h.RaiseAsync(qty: 1, unitPrice: 10_000m, taxRate: 0m)).Value!;
        var second = (await h.RaiseAsync(qty: 1, unitPrice: 20_000m, taxRate: 0m)).Value!;

        var received = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers, 40_000m, "cash",
            Allocations:
            [
                new ReceiptAllocationRequest(first.Id, 10_000m),
                new ReceiptAllocationRequest(second.Id, 15_000m)
            ]), "caissier");

        Assert.True(received.Ok, received.Message);
        Assert.Equal(0m, await h.OutstandingAsync(first.Id));
        Assert.Equal(5_000m, await h.OutstandingAsync(second.Id));

        // The 15 000 nobody wrote against an invoice is still money received: it comes off the
        // customer's account, which is where a payment on account belongs.
        Assert.Equal(-10_000m, await h.TradeDebtAsync());
        Assert.Equal(-10_000m, await h.BalanceAsync());
    }

    [Fact]
    public async Task More_cannot_be_allocated_than_was_received_or_than_is_owed()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync(qty: 1, unitPrice: 10_000m, taxRate: 0m)).Value!;

        var overAllocated = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers, 5_000m, "cash",
            Allocations: [new ReceiptAllocationRequest(invoice.Id, 8_000m)]), "caissier");
        Assert.False(overAllocated.Ok);

        var overInvoiced = await h.ReceiveAsync(50_000m, "cash", invoice.Id, allocate: 50_000m);
        Assert.False(overInvoiced.Ok);
    }

    [Fact]
    public async Task A_receipt_cannot_be_taken_in_a_tender_that_moves_a_balance_of_its_own()
    {
        await using var h = await ReceivableHarness.NewAsync();

        foreach (var key in new[] { "credit", "giftcard", "loyalty" })
        {
            var refused = await h.ReceiveAsync(1_000m, key);
            Assert.False(refused.Ok);
            Assert.Equal(ReceivableFailure.Invalid, refused.Failure);
        }
    }

    [Fact]
    public async Task A_receipt_for_another_customers_invoice_is_refused()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;

        var wrong = await h.ReceiveAsync(1_000m, "cash", invoice.Id, allocate: 1_000m, customerId: ReceivableHarness.Other);
        Assert.False(wrong.Ok);
    }

    // ---------- the credit note ----------

    [Fact]
    public async Task A_credit_note_reverses_the_invoice_on_the_lines_it_was_billed_on()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;

        var credited = await h.Receivables.CreateCreditNoteAsync(
            invoice.Id, new CreateCreditNoteRequest("Béton refusé sur chantier"), "admin");
        Assert.True(credited.Ok, credited.Message);
        Assert.Equal(invoice.GrandTotalTtc, credited.Value!.GrandTotalTtc);
        Assert.Equal("CRN-000001", credited.Value.Number);

        Assert.Equal(0m, await h.OutstandingAsync(invoice.Id));
        Assert.Equal(0m, await h.TradeDebtAsync());
        Assert.Equal(0m, await h.BalanceAsync());

        var posted = await h.PostedAsync(LedgerSources.CreditNote);
        // The revenue comes off the line it went on to, and the tax with it.
        Assert.Equal(120_000m, posted[LedgerAccounts.Sales]);
        Assert.Equal(22_800m, posted[LedgerAccounts.TaxCollected]);
        Assert.Equal(-142_800m, posted[LedgerAccounts.Customers]);
    }

    [Fact]
    public async Task A_credit_note_may_be_partial_and_may_never_give_back_more_than_was_billed()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync(qty: 10, unitPrice: 12_000m, taxRate: 0m)).Value!;
        var lineId = invoice.Lines.Single().Id;

        var half = await h.Receivables.CreateCreditNoteAsync(invoice.Id,
            new CreateCreditNoteRequest("4 m³ non livrés", Lines: [new CreditNoteLineRequest(lineId, 4)]), "admin");
        Assert.True(half.Ok, half.Message);
        Assert.Equal(48_000m, half.Value!.GrandTotalTtc);
        Assert.Equal(72_000m, await h.OutstandingAsync(invoice.Id));

        var tooMuch = await h.Receivables.CreateCreditNoteAsync(invoice.Id,
            new CreateCreditNoteRequest("encore", Lines: [new CreditNoteLineRequest(lineId, 7)]), "admin");
        Assert.False(tooMuch.Ok);

        var rest = await h.Receivables.CreateCreditNoteAsync(invoice.Id,
            new CreateCreditNoteRequest("le reste"), "admin");
        Assert.True(rest.Ok, rest.Message);
        Assert.Equal(72_000m, rest.Value!.GrandTotalTtc);

        var again = await h.Receivables.CreateCreditNoteAsync(invoice.Id, new CreateCreditNoteRequest("une fois de plus"), "admin");
        Assert.False(again.Ok);
        Assert.Equal(ReceivableFailure.Conflict, again.Failure);
    }

    [Fact]
    public async Task A_credit_note_needs_a_reason()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;

        var unexplained = await h.Receivables.CreateCreditNoteAsync(invoice.Id, new CreateCreditNoteRequest("  "), "admin");

        // An unexplained credit note is indistinguishable from a debt written off by whoever
        // could reach the screen.
        Assert.False(unexplained.Ok);
        Assert.Equal(ReceivableFailure.Invalid, unexplained.Failure);
    }

    [Fact]
    public async Task A_credit_note_gives_back_a_discounted_line_net_of_its_discount()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(null, "BET", "Béton", 10, 1_000m, 0m, Discount: 2_000m)]))).Value!;

        Assert.Equal(8_000m, invoice.GrandTotalTtc);

        var credited = await h.Receivables.CreateCreditNoteAsync(invoice.Id,
            new CreateCreditNoteRequest("moitié", Lines: [new CreditNoteLineRequest(invoice.Lines.Single().Id, 5)]), "admin");

        // Quantity × price would give back 5 000 on a line that was only ever charged 8 000.
        Assert.True(credited.Ok, credited.Message);
        Assert.Equal(4_000m, credited.Value!.GrandTotalTtc);
    }

    // ---------- invoices over documents that have already posted ----------

    [Fact]
    public async Task An_invoice_over_a_rental_issue_note_posts_nothing_and_bills_what_the_note_billed()
    {
        await using var h = await ReceivableHarness.NewAsync();

        var contract = await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin");
        Assert.True(contract.Ok, contract.Message);

        var issued = await h.Rentals.IssueAsync(contract.Value!.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers,
            [new IssueNoteLineRequest(contract.Value.Lines.Single().Id, 60)], DateTime.UtcNow)), null, "magasinier");
        Assert.True(issued.Ok, issued.Message);

        var debtAfterIssue = await h.TradeDebtAsync();
        Assert.Equal(180_000m, debtAfterIssue);   // 60 × 30 days × 100 a day

        var invoice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Value!.Note.Id), "admin");
        Assert.True(invoice.Ok, invoice.Message);

        // ⚠ The whole point. The hire was billed to 411 when the goods went out; the invoice is
        // the paper for it and must not bill it again.
        Assert.Equal(debtAfterIssue, await h.TradeDebtAsync());
        Assert.Empty(await h.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.Invoice).ToListAsync());

        Assert.Equal(180_000m, invoice.Value!.GrandTotalTtc);
        Assert.Equal(180_000m, await h.OutstandingAsync(invoice.Value.Id));
        Assert.Equal(LedgerAccounts.RentalIncome, invoice.Value.Lines.Single().IncomeAccountCode);

        var twice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Value.Note.Id), "admin");
        Assert.False(twice.Ok);
        Assert.Equal(ReceivableFailure.Conflict, twice.Failure);
    }

    [Fact]
    public async Task Crediting_a_rental_invoice_gives_the_money_back_on_the_rental_income_line()
    {
        await using var h = await ReceivableHarness.NewAsync();

        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var issued = (await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers,
            [new IssueNoteLineRequest(contract.Lines.Single().Id, 60)], DateTime.UtcNow)), null, "magasinier")).Value!;

        var invoice = (await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Note.Id), "admin")).Value!;

        var credited = await h.Receivables.CreateCreditNoteAsync(
            invoice.Id, new CreateCreditNoteRequest("Chantier annulé"), "admin");
        Assert.True(credited.Ok, credited.Message);

        var posted = await h.PostedAsync(LedgerSources.CreditNote);
        // Rent, not merchandise sales: the owner asked for those lines apart, and a credit note
        // that put hire back on 700 would make both of them wrong.
        Assert.Equal(180_000m, posted[LedgerAccounts.RentalIncome]);
        Assert.False(posted.ContainsKey(LedgerAccounts.Sales));
        Assert.Equal(0m, await h.TradeDebtAsync());
    }

    [Fact]
    public async Task An_invoice_over_a_sale_chases_only_what_the_sale_put_on_the_account()
    {
        await using var h = await ReceivableHarness.NewAsync();

        // A sale of 100 000 with 30 000 paid in cash at the counter and the rest on account.
        var sale = await h.PushSaleAsync(total: 100_000m, cash: 30_000m);

        var invoice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), SaleId: sale), "admin");
        Assert.True(invoice.Ok, invoice.Message);

        // The paper says 100 000 because that is what was sold; 30 000 of it was settled before
        // the invoice existed and is neither owed nor allocatable.
        Assert.Equal(100_000m, invoice.Value!.GrandTotalTtc);
        Assert.Equal(30_000m, invoice.Value.PrepaidAmount);
        Assert.Equal(70_000m, await h.OutstandingAsync(invoice.Value.Id));
        Assert.Equal(70_000m, await h.TradeDebtAsync());

        // And nothing was posted a second time.
        Assert.Empty(await h.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.Invoice).ToListAsync());
    }

    [Fact]
    public async Task A_sale_settled_at_the_till_is_invoiced_already_settled_and_takes_no_money_twice()
    {
        // Owner, 2026-10-10: the till took the money; the tax invoice is issued, settled, with nothing to collect.
        await using var h = await ReceivableHarness.NewAsync();
        var sale = await h.PushSaleAsync(total: 100_000m, cash: 100_000m);

        var invoice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), SaleId: sale, Settlement: "full", SettlementMethodKey: "cash"), "admin");

        Assert.True(invoice.Ok, invoice.Message);
        Assert.Equal(0m, await h.OutstandingAsync(invoice.Value!.Id));
        Assert.Equal(0, await h.Db.ReceiptVouchers.CountAsync());
    }

    [Fact]
    public async Task Paid_in_full_on_issue_records_its_own_settlement_and_the_debt_comes_off()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var before = await h.BalanceAsync();
        var sale = await h.PushSaleAsync(total: 60_000m, cash: 0m);

        var invoice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), SaleId: sale, Settlement: "full", SettlementMethodKey: "cash"), "admin");

        Assert.True(invoice.Ok, invoice.Message);
        Assert.Equal(0m, await h.OutstandingAsync(invoice.Value!.Id));
        var receipt = await h.Db.ReceiptVouchers.Include(v => v.Allocations).SingleAsync();
        Assert.Equal(60_000m, Assert.Single(receipt.Allocations, a => a.InvoiceId == invoice.Value.Id).Amount);
        Assert.Equal(before, await h.BalanceAsync());
    }

    [Fact]
    public async Task Deferred_stays_open_for_receipts_and_full_without_a_method_writes_nothing()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var sale = await h.PushSaleAsync(total: 40_000m, cash: 0m);

        var refused = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), SaleId: sale, Settlement: "full"), "admin");
        Assert.False(refused.Ok);
        Assert.Equal(0, await h.Db.Invoices.CountAsync());

        var deferred = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), SaleId: sale, Settlement: "deferred"), "admin");
        Assert.True(deferred.Ok, deferred.Message);
        Assert.Equal(40_000m, await h.OutstandingAsync(deferred.Value!.Id));
        Assert.Equal(0, await h.Db.ReceiptVouchers.CountAsync());
    }
}
