using ParadisERP.Application.Contracts;
using ParadisERP.Application.Receivables;
using ParadisERP.Domain;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Rentals.RentalTestPaper;

namespace ParadisERP.Tests.Receivables;

/// <summary>
/// The statement against the books, on documents nobody chose.
///
/// <para>
/// A hand-picked case proves that one arrangement of documents reconciles. What has to be true
/// is that <b>every</b> arrangement does — and the arrangements that break a debtor book are
/// never the ones someone thought to write a test for: a cheque that bounced after a credit
/// note, a receipt spread over three invoices two of which were later credited, a rental note
/// invoiced on the day its contract closed. So the documents here are generated from a seed,
/// several hundred of them, and four things are asserted about whatever comes out.
/// </para>
/// <para>
/// The one that matters most is the third. The first is arithmetic about a single query; the
/// third computes what the customer must owe from the documents alone — every invoice raised,
/// less every payment that actually arrived, less every credit note — and requires the ledger to
/// have reached the same figure by an entirely different route.
/// </para>
/// </summary>
public class CustomerStatementTests
{
    private const decimal RentalTaxRate = 19m;

    [Fact]
    public async Task A_statement_opens_where_the_last_one_closed_and_lists_what_happened_between()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var early = DateTime.UtcNow.AddDays(-60);
        var mid = DateTime.UtcNow.AddDays(-30);

        await h.RaiseAsync(new CreateInvoiceRequest(
            early.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers, IssuedAtUtc: early,
            Lines: [new InvoiceLineRequest(null, "A", "A", 1, 100_000m, 0m)]));
        await h.RaiseAsync(new CreateInvoiceRequest(
            mid.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers, IssuedAtUtc: mid.AddDays(1),
            Lines: [new InvoiceLineRequest(null, "B", "B", 1, 40_000m, 0m)]));

        var first = await h.Statements.BuildAsync(ReceivableHarness.Contractor, early.AddDays(-1), mid);
        var second = await h.Statements.BuildAsync(ReceivableHarness.Contractor, mid, DateTime.UtcNow.AddDays(1));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(0m, first!.OpeningBalance);
        Assert.Equal(100_000m, first.ClosingBalance);
        // The join between two periods: no movement may fall down it and none may be counted twice.
        Assert.Equal(first.ClosingBalance, second!.OpeningBalance);
        Assert.Equal(140_000m, second.ClosingBalance);
        Assert.Single(second.Rows);
        Assert.Equal("INV-000002", second.Rows[0].DocumentNumber);
    }

    [Fact]
    public async Task Ageing_as_of_a_past_day_does_not_see_what_was_paid_or_credited_since()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var now = DateTime.UtcNow;
        var issued = now.AddDays(-60);
        var invoice = (await h.RaiseAsync(new CreateInvoiceRequest(
            issued.AddDays(20), ReceivableHarness.Contractor, ReceivableHarness.Algiers, IssuedAtUtc: issued,
            Lines: [new InvoiceLineRequest(null, "A", "A", 1, 100_000m, 0m)]))).Value!;
        var other = (await h.RaiseAsync(new CreateInvoiceRequest(
            issued.AddDays(20), ReceivableHarness.Contractor, ReceivableHarness.Algiers, IssuedAtUtc: issued,
            Lines: [new InvoiceLineRequest(null, "B", "B", 1, 40_000m, 0m)]))).Value!;

        // Today: one is paid in cash, the other credited in full.
        Assert.True((await h.ReceiveAsync(100_000m, "cash", invoice.Id)).Ok);
        Assert.True((await h.Receivables.CreateCreditNoteAsync(
            other.Id, new CreateCreditNoteRequest("Erreur de facturation"), "admin")).Ok);

        // Ten days ago both were owed and thirty days late; the report for that day must say so,
        // and its own 411 balance (cut at the same instant) must agree with it.
        var then = await h.Statements.AgeAsync(now.AddDays(-10), ReceivableHarness.Contractor);
        var row = Assert.Single(then.Customers);
        Assert.Equal(140_000m, row.Total);
        Assert.Equal(140_000m, row.Days1To30);
        Assert.Equal(30, row.OldestDaysOverdue);
        Assert.Equal(0m, row.Uninvoiced);
        Assert.Equal(row.LedgerBalance, row.Total);

        // And today nothing is owed, so the customer is not on the board at all. Read the clock
        // again: the receipt and the credit note were stamped after `now` was taken.
        Assert.Empty((await h.Statements.AgeAsync(DateTime.UtcNow, ReceivableHarness.Contractor)).Customers);
    }

    [Fact]
    public async Task A_statement_shows_one_customer_and_never_another()
    {
        await using var h = await ReceivableHarness.NewAsync();
        await h.RaiseAsync(qty: 1, unitPrice: 100_000m, taxRate: 0m);
        await h.RaiseAsync(ReceivableHarness.DirectInvoice(
            qty: 1, unitPrice: 7_000m, taxRate: 0m, customerId: ReceivableHarness.Other));

        var statement = await h.Statements.BuildAsync(
            ReceivableHarness.Contractor, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        Assert.Equal(100_000m, statement!.ClosingBalance);
        Assert.Single(statement.Rows);
    }

    [Theory]
    [InlineData(20260923)]
    [InlineData(1)]
    [InlineData(4_815_162)]
    // Was 99_991 until 2026-10-10: rental invoices carry their real quantities since, so a credit
    // note takes part of one where it used to round to nothing, and that seed's path no longer
    // happened to bounce a cheque — which the coverage check below asks of every seed.
    [InlineData(42)]
    public async Task The_statement_the_ageing_and_the_ledger_agree_on_whatever_is_generated(int seed)
    {
        await using var h = await ReceivableHarness.NewAsync(creditLimit: 4_000_000m, rentalTaxRate: RentalTaxRate);
        var random = new Random(seed);

        // Computed as the documents are raised, from the documents alone. Nothing here reads
        // the ledger, which is the point: this figure and account 411 arrive at the same number
        // by two entirely separate routes or one of them is wrong.
        var expected = 0m;
        var invoices = new List<Guid>();
        var pending = new List<Guid>();

        for (var i = 0; i < 60; i++)
        {
            switch (random.Next(7))
            {
                case 0: expected += await DirectInvoiceAsync(h, random, invoices); break;
                case 1: expected -= await ReceiveAsync(h, random, invoices, "cash"); break;
                case 2: await ChequeAsync(h, random, invoices, pending); break;
                case 3: expected += await SettleAsync(h, random, pending); break;
                case 4: expected -= await CreditAsync(h, random, invoices); break;
                case 5: expected += await RentalAsync(h, random, invoices); break;
                case 6: expected += await SaleAsync(h, random); break;
            }
        }

        // A generator that quietly refused everything would satisfy every assertion below on an
        // empty ledger, so it has to be able to say it produced each kind of document.
        Assert.True(await h.Db.Invoices.CountAsync() >= 5);
        Assert.True(await h.Db.CreditNotes.AnyAsync());
        Assert.True(await h.Db.ReceiptVouchers.AnyAsync(v => v.Status == PaymentStatus.Confirmed));
        Assert.True(await h.Db.ReceiptVouchers.AnyAsync(v => v.Status == PaymentStatus.Bounced));
        Assert.True(await h.Db.ReceiptVouchers.AnyAsync(v => v.Status == PaymentStatus.Pending));
        Assert.True(await h.Db.IssueNotes.AnyAsync());
        Assert.True(await h.Db.Sales.AnyAsync());

        var window = (From: DateTime.UtcNow.AddYears(-1), To: DateTime.UtcNow.AddYears(1));
        var statement = await h.Statements.BuildAsync(ReceivableHarness.Contractor, window.From, window.To);
        Assert.NotNull(statement);

        var trade = await h.TradeDebtAsync();
        var uncleared = await h.UnclearedAsync();

        // 1. The statement is account 411 for this customer, and says so.
        Assert.Equal(trade, statement!.ClosingBalance);
        Assert.Equal(statement.OpeningBalance + statement.TotalDebit - statement.TotalCredit, statement.ClosingBalance);
        Assert.Equal(statement.ClosingBalance, statement.Rows.Count == 0 ? statement.OpeningBalance : statement.Rows[^1].Balance);

        // 2. Nothing falls down the join between two periods and nothing is counted twice.
        var mid = DateTime.UtcNow.AddDays(-15);
        var before = await h.Statements.BuildAsync(ReceivableHarness.Contractor, window.From, mid);
        var after = await h.Statements.BuildAsync(ReceivableHarness.Contractor, mid, window.To);
        Assert.Equal(before!.ClosingBalance, after!.OpeningBalance);
        Assert.Equal(statement.ClosingBalance, after.ClosingBalance);
        Assert.Equal(statement.Rows.Count, before.Rows.Count + after.Rows.Count);

        // 3. ⚠ The one that is not arithmetic about a single query: what the documents say the
        // customer owes, and what the books say, computed independently of each other.
        Assert.Equal(expected, trade);

        // 4. The denormalised balance is trade debt plus the paper that has not cleared. A
        // pending cheque is in neither of the first two and in both of these.
        Assert.Equal(trade + uncleared, await h.BalanceAsync());
        Assert.Equal(uncleared, statement.PendingNotes);
        Assert.Equal(trade + uncleared, statement.TotalOwed);
        Assert.Equal(0m, statement.BalanceDifference);

        // 5. Invoiced debt plus what has no invoice behind it is the whole balance — the gap is
        // stated rather than left for a reader to discover.
        Assert.Equal(statement.ClosingBalance, statement.InvoicedOutstanding + statement.UninvoicedBalance);

        // 6. The ageing report is the same debt sliced by age.
        var ageing = await h.Statements.AgeAsync(DateTime.UtcNow, ReceivableHarness.Contractor);
        var row = Assert.Single(ageing.Customers);
        Assert.Equal(trade, row.LedgerBalance);
        Assert.Equal(
            row.Current + row.Days1To30 + row.Days31To60 + row.Days61To90 + row.Over90, row.Total);
        Assert.Equal(statement.InvoicedOutstanding, row.Total - row.CreditBalance);
        Assert.Equal(row.LedgerBalance - (row.Total - row.CreditBalance), row.Uninvoiced);

        // 7. Every entry any of this raised still balances, and no pending voucher reached the
        // ledger at all.
        await AssertBooksBalanceAsync(h);
        foreach (var id in await h.Db.ReceiptVouchers.Where(v => v.Status == PaymentStatus.Pending)
                     .Select(v => v.Id).ToListAsync())
            Assert.Empty(await h.Db.LedgerEntries
                .Where(e => e.SourceType == LedgerSources.CustomerReceipt && e.SourceId == id).ToListAsync());
    }

    // ---------- the generator ----------

    private static async Task<decimal> DirectInvoiceAsync(ReceivableHarness h, Random random, List<Guid> invoices)
    {
        var issued = DateTime.UtcNow.AddDays(-random.Next(0, 200));
        var lines = Enumerable.Range(0, random.Next(1, 4))
            .Select(_ => new InvoiceLineRequest(
                null, "SKU", "Ligne",
                random.Next(1, 40), Money(random, 500, 90_000), random.Next(2) == 0 ? 19m : 9m,
                random.Next(4) == 0 ? Money(random, 10, 400) : 0m))
            .ToList();

        var raised = await h.Receivables.CreateInvoiceAsync(new CreateInvoiceRequest(
            issued.AddDays(random.Next(0, 90)), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            IssuedAtUtc: issued,
            TaxMode: random.Next(5) == 0 ? nameof(TaxMode.Untaxed) : null,
            Lines: lines), "admin");

        // A discount can exceed a small line; that is a refusal, not a failure of the test.
        if (!raised.Ok) return 0m;
        invoices.Add(raised.Value!.Id);
        return raised.Value.GrandTotalTtc;
    }

    private static async Task<decimal> ReceiveAsync(
        ReceivableHarness h, Random random, List<Guid> invoices, string method)
    {
        var amount = Money(random, 1_000, 300_000);
        var allocations = await AllocateAsync(h, random, invoices, amount);

        var received = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers, amount, method,
            ReceivedAtUtc: DateTime.UtcNow.AddDays(-random.Next(0, 120)),
            Allocations: allocations), "caissier");

        return received.Ok ? received.Value!.Amount : 0m;
    }

    /// <summary>A cheque changes nothing at all, so it returns nothing to the expected figure.</summary>
    private static async Task ChequeAsync(
        ReceivableHarness h, Random random, List<Guid> invoices, List<Guid> pending)
    {
        var amount = Money(random, 1_000, 300_000);
        var allocations = await AllocateAsync(h, random, invoices, amount);

        var received = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers, amount, "cheque",
            ReceivedAtUtc: DateTime.UtcNow.AddDays(-random.Next(0, 120)),
            InstrumentNumber: random.Next(100_000, 999_999).ToString(),
            InstrumentBank: "BEA",
            InstrumentDueDate: DateTime.UtcNow.AddDays(random.Next(-30, 60)),
            Allocations: allocations), "caissier");

        if (received.Ok) pending.Add(received.Value!.Id);
    }

    private static async Task<decimal> SettleAsync(ReceivableHarness h, Random random, List<Guid> pending)
    {
        if (pending.Count == 0) return 0m;

        var index = random.Next(pending.Count);
        var id = pending[index];
        pending.RemoveAt(index);

        // Two cheques in three clear. A bounce moves nothing, which is exactly what has to be
        // true of it.
        var confirmed = random.Next(3) != 0;
        var settled = await h.Receivables.SettleReceiptAsync(
            id, confirmed ? nameof(PaymentStatus.Confirmed) : nameof(PaymentStatus.Bounced), "auto", "admin");

        return settled.Ok && confirmed ? -settled.Value!.Amount : 0m;
    }

    private static async Task<decimal> CreditAsync(ReceivableHarness h, Random random, List<Guid> invoices)
    {
        if (invoices.Count == 0) return 0m;

        var invoiceId = invoices[random.Next(invoices.Count)];
        var invoice = await h.Receivables.ReadInvoiceAsync(invoiceId);
        if (invoice is null) return 0m;

        var already = await h.Receivables.CreditedQtyByLineAsync(invoiceId);
        var lines = invoice.Lines
            .Select(l => new CreditNoteLineRequest(
                l.Id, Math.Floor((l.Qty - already.GetValueOrDefault(l.Id)) * (random.Next(1, 4) / 4m))))
            .Where(l => l.Qty > 0)
            .ToList();
        if (lines.Count == 0) return 0m;

        var credited = await h.Receivables.CreateCreditNoteAsync(
            invoiceId, new CreateCreditNoteRequest("généré", DateTime.UtcNow.AddDays(-random.Next(0, 90)), lines),
            "admin");

        return credited.Ok ? credited.Value!.GrandTotalTtc : 0m;
    }

    /// <summary>
    /// Goods out on hire, and sometimes the invoice for them. Either way the money reached 411
    /// when the lorry left — which is what the invoice must not do a second time.
    /// </summary>
    private static async Task<decimal> RentalAsync(ReceivableHarness h, Random random, List<Guid> invoices)
    {
        var qty = random.Next(1, 20);
        var contract = await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(
                ReceivableHarness.Scaffold, qty, random.Next(7, 90),
                RentUnitPrice: Money(random, 1_000, 6_000), DamageUnitPrice: 2_000m)])), "admin");
        if (!contract.Ok) return 0m;

        var issued = await h.Rentals.IssueAsync(contract.Value!.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers, [new IssueNoteLineRequest(contract.Value.Lines.Single().Id, qty)],
            DateTime.UtcNow.AddDays(-random.Next(0, 120)))), null, "magasinier");
        if (!issued.Ok) return 0m;

        var rent = issued.Value!.PeriodRent;
        var tax = Math.Round(rent * RentalTaxRate / 100m, 2, MidpointRounding.AwayFromZero);

        if (random.Next(2) == 0)
        {
            var invoice = await h.Receivables.CreateInvoiceAsync(
                new CreateInvoiceRequest(DateTime.UtcNow.AddDays(random.Next(0, 60)),
                    IssueNoteId: issued.Value.Note.Id), "admin");
            if (invoice.Ok) invoices.Add(invoice.Value!.Id);
        }

        return rent + tax;
    }

    /// <summary>A till sale on account, pushed the way a real one is.</summary>
    private static async Task<decimal> SaleAsync(ReceivableHarness h, Random random)
    {
        var total = Money(random, 5_000, 200_000);
        var cash = random.Next(3) == 0 ? Money(random, 0, total) : 0m;
        await h.PushSaleAsync(total, cash);
        return total - cash;
    }

    private static async Task<List<ReceiptAllocationRequest>?> AllocateAsync(
        ReceivableHarness h, Random random, List<Guid> invoices, decimal amount)
    {
        // A third of the money is paid on account with nothing written against it, which is how
        // a contractor's transfer usually arrives.
        if (invoices.Count == 0 || random.Next(3) == 0) return null;

        var standings = await h.Receivables.StandingsAsync(invoices);
        var allocations = new List<ReceiptAllocationRequest>();
        var left = amount;

        foreach (var id in invoices.OrderBy(_ => random.Next()).Take(random.Next(1, 4)))
        {
            var allocatable = Math.Min(left, standings[id].Allocatable);
            if (allocatable <= 0) continue;
            var take = Math.Round(allocatable * (random.Next(1, 5) / 4m), 2, MidpointRounding.ToZero);
            if (take <= 0) continue;
            allocations.Add(new ReceiptAllocationRequest(id, take));
            left -= take;
        }

        return allocations.Count == 0 ? null : allocations;
    }

    private static decimal Money(Random random, decimal low, decimal high) =>
        Math.Round(low + (decimal)random.NextDouble() * (high - low), 2, MidpointRounding.AwayFromZero);

    /// <summary>Every entry balances, and therefore so do the books. Summed in memory: SQLite stores decimal as TEXT.</summary>
    private static async Task AssertBooksBalanceAsync(ReceivableHarness h)
    {
        var entries = await h.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).ToListAsync();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
            Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
    }
}
