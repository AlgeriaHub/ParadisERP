using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Receivables.ReceivableHarness;

namespace ParadisERP.Tests.Receivables;

/// <summary>
/// SAL-13 (owner 2026-11-10, answer A; docs/accounting/advances.md): part of a customer's money kept on a cancelled order,
/// moved to the "other income" account the accountant chose — as the accountant's setting, not allowed by default. From an
/// advance on 419 (Dr 419 / Cr income; with VAT when received its VAT part stays due on 4457), or from a receipt's
/// unallocated credit on 411 (Dr 411 / Cr income). The customer's statement shows it as its own debit row and still reads
/// what the customer is owed.
/// </summary>
public class CustomerAdvanceKeptTests
{
    /// <summary>The accountant's "other income" account for kept advances (a class 7 account added to the chart).</summary>
    private const string OtherIncome = "7585";

    private static async Task AddIncomeAccountAsync(ReceivableHarness h, string code = OtherIncome, bool active = true)
    {
        h.Db.LedgerAccounts.Add(new LedgerAccount
        {
            Code = code, NameAr = "إيرادات أخرى", NameFr = "Autres produits — acomptes conservés", NameEn = "Other income — kept advances",
            Type = LedgerAccountType.Income, IsActive = active, SortOrder = 99,
        });
        await h.Db.SaveChangesAsync();
    }

    private static async Task SetAsync(ReceivableHarness h, string heldOn, string vat = "AtInvoice", string kept = "OtherIncome", string? account = OtherIncome)
    {
        var saved = await new ReceivableSettingsStore(h.Db).UpdateAsync(new UpdateReceivableSettingsRequest(heldOn, vat, kept, account), "comptable");
        Assert.True(saved.Saved is not null, saved.Error);
        h.Db.ChangeTracker.Clear();
    }

    private static Task<ReceivableResult<ReceiptVoucher>> AdvanceAsync(ReceivableHarness h, decimal amount, decimal? rate = null) =>
        h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, amount, "cash", Kind: "advance", AdvanceTaxRate: rate), "caissier");

    private async static Task<ReceivableResult<ReceiptVoucher>> KeepAsync(ReceivableHarness h, Guid id, decimal? amount, string? reason = "Commande annulée")
    {
        var result = await h.Receivables.KeepAdvanceAsync(id, new KeepAdvanceRequest(amount, reason), "comptable");
        h.Db.ChangeTracker.Clear();
        return result;
    }

    /// <summary>What one entry posted, by account; a customer's 4457 line and an untagged one kept apart.</summary>
    private static async Task<Dictionary<string, decimal>> EntryAsync(ReceivableHarness h, string source, Guid id)
    {
        var accounts = await h.Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var entry = await h.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.SourceType == source && e.SourceId == id);
        return entry.Lines
            .GroupBy(l => accounts[l.AccountId] + (l.CustomerId is null ? "" : "/c"))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    private static Task<CustomerStatementDto?> StatementAsync(ReceivableHarness h) =>
        h.Statements.BuildAsync(Contractor, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

    private static PaymentVouchers Vouchers(ReceivableHarness h) => new(h.Db, new LedgerPoster(h.Db), new AlgeriaForTests());

    // ---------- the default ----------

    [Fact]
    public async Task By_default_keeping_is_not_allowed_and_nothing_moves()
    {
        await using var h = await NewAsync();
        await AddIncomeAccountAsync(h);
        var settings = await new ReceivableSettingsStore(h.Db).SettingsAsync();
        Assert.Equal(("NotAllowed", (string?)null), (settings.KeptAdvances, settings.KeptIncomeAccount));
        Assert.Contains(settings.IncomeAccounts!, a => a.Code == OtherIncome);
        Assert.Contains(settings.IncomeAccounts!, a => a.Code == LedgerAccounts.Sales);
        Assert.DoesNotContain(settings.IncomeAccounts!, a => !a.Code.StartsWith('7'));

        var receipt = (await h.ReceiveAsync(100_000m)).Value!;
        var refused = await KeepAsync(h, receipt.Id, 10_000m);
        Assert.Equal((ReceivableFailure.Invalid, ReceivableDocuments.KeptAdvancesNotAllowedCode), (refused.Failure, refused.Code));

        // On 419 too: the setting decides, not where the money sits.
        await SetAsync(h, "AdvancesAccount", kept: "NotAllowed", account: null);
        var advance = (await AdvanceAsync(h, 50_000m)).Value!;
        Assert.Equal(ReceivableFailure.Invalid, (await KeepAsync(h, advance.Id, null)).Failure);

        Assert.False(await h.Db.LedgerEntries.AnyAsync(e => e.SourceType == LedgerSources.CustomerAdvanceKept));
        Assert.Equal(0, await h.Db.CustomerAdvanceApplications.CountAsync());
        Assert.Equal(-150_000m, await h.BalanceAsync());
    }

    // ---------- the setting ----------

    [Fact]
    public async Task Kept_as_other_income_needs_an_active_class_7_income_account()
    {
        await using var h = await NewAsync();
        var store = new ReceivableSettingsStore(h.Db);

        // None chosen, not an income account, not class 7, inactive: each refused, nothing saved.
        foreach (var account in new string?[] { null, "", LedgerAccounts.Cash, "999", OtherIncome })
        {
            var refused = await store.UpdateAsync(new("CustomerAccount", "AtInvoice", "OtherIncome", account), "comptable");
            Assert.Equal((409, ReceivableSettingsStore.AccountsNotMappedCode), (refused.Status, refused.Code));
        }
        await AddIncomeAccountAsync(h, active: false);
        Assert.Equal(409, (await store.UpdateAsync(new("CustomerAccount", "AtInvoice", "OtherIncome", OtherIncome), "comptable")).Status);
        Assert.Equal(400, (await store.UpdateAsync(new("CustomerAccount", "AtInvoice", "Sometimes", null), "comptable")).Status);
        Assert.Equal(0, await h.Db.ReceivableSettings.CountAsync());

        var account7585 = await h.Db.LedgerAccounts.SingleAsync(a => a.Code == OtherIncome);
        account7585.IsActive = true;
        await h.Db.SaveChangesAsync();
        var saved = (await store.UpdateAsync(new("CustomerAccount", "AtInvoice", "OtherIncome", OtherIncome), "comptable")).Saved!;
        Assert.Equal(("OtherIncome", OtherIncome), (saved.KeptAdvances, saved.KeptIncomeAccount));

        // An older client that sends only the first two choices keeps the SAL-13 ones as saved.
        var older = (await store.UpdateAsync(new("CustomerAccount", "AtInvoice"), "comptable")).Saved!;
        Assert.Equal(("OtherIncome", OtherIncome), (older.KeptAdvances, older.KeptIncomeAccount));

        // Back to Not allowed is always possible.
        var off = (await store.UpdateAsync(new("CustomerAccount", "AtInvoice", "NotAllowed", null), "comptable")).Saved!;
        Assert.Equal("NotAllowed", off.KeptAdvances);
    }

    // ---------- an advance on 419 ----------

    [Fact]
    public async Task On_419_an_untaxed_advance_is_kept_in_part_the_rest_refunded_and_the_statement_shows_the_kept_row()
    {
        await using var h = await NewAsync();
        await AddIncomeAccountAsync(h);
        await SetAsync(h, "AdvancesAccount");
        var advance = (await AdvanceAsync(h, 100_000m)).Value!;

        // A reason is required; more than the advance holds is refused; nothing posted.
        Assert.Equal(ReceivableFailure.Invalid, (await KeepAsync(h, advance.Id, 30_000m, reason: "  ")).Failure);
        Assert.Equal(ReceivableFailure.Invalid, (await KeepAsync(h, advance.Id, 0m)).Failure);
        Assert.Equal(ReceivableFailure.Invalid, (await KeepAsync(h, advance.Id, 100_000.01m)).Failure);

        var kept = await KeepAsync(h, advance.Id, 30_000m);
        Assert.True(kept.Ok, kept.Message);
        var row = await h.Db.CustomerAdvanceApplications.AsNoTracking().SingleAsync();
        Assert.Equal((AdvanceMoveKind.Kept, 30_000m, 0m, "Commande annulée", OtherIncome, (Guid?)null),
            (row.Kind, row.Amount, row.TaxAmount, row.Reason, row.IncomeAccountCode, row.InvoiceId));
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.CustomerDeposits + "/c"] = 30_000m, [OtherIncome + "/c"] = -30_000m },
            await EntryAsync(h, LedgerSources.CustomerAdvanceKept, row.Id));

        // 70,000 still on 419; the customer is owed 70,000.
        Assert.Equal(70_000m, (await CustomerAdvances.OpenAsync(h.Db, Contractor))[advance.Id]);
        Assert.Equal((-70_000m, -70_000m), (await h.AccountAsync(LedgerAccounts.CustomerDeposits, Contractor), await h.BalanceAsync()));

        var statement = (await StatementAsync(h))!;
        Assert.Equal((-70_000m, 0m), (statement.ClosingBalance, statement.BalanceDifference));
        Assert.Equal(2, statement.Rows.Count);
        var keptRow = Assert.Single(statement.Rows, r => r.SourceType == LedgerSources.CustomerAdvanceKept);
        Assert.Equal((30_000m, 0m, advance.Number, "Advance kept — Commande annulée", "adjustment"),
            (keptRow.Debit, keptRow.Credit, keptRow.DocumentNumber, keptRow.Description, keptRow.Kind));
        Assert.Equal(-70_000m, (await h.Statements.AgeAsync(DateTime.UtcNow.AddMinutes(1), Contractor)).Customers.Single().LedgerBalance);

        // The rest is refunded the usual way: released to 411, then a refund voucher capped at it.
        Assert.True((await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(null), "x")).Ok);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(ReceivableFailure.Conflict,
            (await Vouchers(h).CreateAsync(new CreatePaymentVoucherRequest(Algiers, PaymentVoucherKind.CustomerRefund, 70_000.01m, "cash", CustomerId: Contractor), "caissier")).Failure);
        Assert.True((await Vouchers(h).CreateAsync(new CreatePaymentVoucherRequest(Algiers, PaymentVoucherKind.CustomerRefund, 70_000m, "cash", CustomerId: Contractor), "caissier")).Ok);

        // Nothing left to keep; every entry balanced; income carries the kept 30,000.
        Assert.Equal(ReceivableFailure.Conflict, (await KeepAsync(h, advance.Id, null)).Failure);
        var posted = await h.PostedAsync();
        Assert.Equal((0m, -30_000m), (posted.Values.Sum(), posted[OtherIncome]));
    }

    [Fact]
    public async Task With_VAT_when_received_the_kept_part_leaves_its_VAT_due_and_the_rest_gives_back_exactly_the_remainder()
    {
        await using var h = await NewAsync();
        await AddIncomeAccountAsync(h);
        await SetAsync(h, "AdvancesAccount", "WhenReceived");

        // 119,000 at 19 %: 19,000 of VAT declared on receipt.
        var advance = (await AdvanceAsync(h, 119_000m, rate: 19m)).Value!;
        Assert.Equal(19_000m, advance.AdvanceTaxAmount);

        // Half kept: the net 50,000 goes 419 → income; its 9,500 of VAT stays on 4457 (still due), only moved off the customer.
        Assert.True((await KeepAsync(h, advance.Id, 59_500m)).Ok);
        var row = await h.Db.CustomerAdvanceApplications.AsNoTracking().SingleAsync();
        Assert.Equal(9_500m, row.TaxAmount);
        Assert.Equal(new Dictionary<string, decimal>
        {
            [LedgerAccounts.CustomerDeposits + "/c"] = 50_000m, [LedgerAccounts.TaxCollected + "/c"] = 9_500m,
            [LedgerAccounts.TaxCollected] = -9_500m, [OtherIncome + "/c"] = -50_000m,
        }, await EntryAsync(h, LedgerSources.CustomerAdvanceKept, row.Id));
        Assert.Equal(-19_000m, (await h.PostedAsync())[LedgerAccounts.TaxCollected]);  // nothing given back by the keep

        var mid = (await StatementAsync(h))!;
        Assert.Equal((-59_500m, 0m), (mid.ClosingBalance, mid.BalanceDifference));
        Assert.Equal(59_500m, Assert.Single(mid.Rows, r => r.SourceType == LedgerSources.CustomerAdvanceKept).Debit);

        // The order goes on with an invoice of 120,000 + 22,800; the rest of the advance is applied to it and gives back
        // only the rest of its VAT (9,500).
        var invoice = (await h.RaiseAsync()).Value!;
        Assert.True((await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(invoice.Id), "x")).Ok);
        h.Db.ChangeTracker.Clear();
        var applied = await h.Db.CustomerAdvanceApplications.AsNoTracking().SingleAsync(a => a.Kind == AdvanceMoveKind.Application);
        Assert.Equal((59_500m, 9_500m), (applied.Amount, applied.TaxAmount));

        // Net 4457 = −19,000 (advance) + 0 (keep) − 22,800 (invoice) + 9,500 (application): the invoice's VAT and the kept
        // part's, each once. Nothing of it is the customer's any more; 419 is empty.
        var posted = await h.PostedAsync();
        Assert.Equal(-32_300m, posted[LedgerAccounts.TaxCollected]);
        Assert.Equal((-50_000m, 0m), (posted[OtherIncome], posted.Values.Sum()));
        Assert.Equal((0m, 0m), (await h.AccountAsync(LedgerAccounts.TaxCollected, Contractor), await h.AccountAsync(LedgerAccounts.CustomerDeposits, Contractor)));
        Assert.Equal(83_300m, await h.OutstandingAsync(invoice.Id));
        var after = (await StatementAsync(h))!;
        Assert.Equal((83_300m, 0m, 83_300m), (after.ClosingBalance, after.BalanceDifference, after.RecordedBalance));
    }

    [Fact]
    public async Task An_odd_keep_rounds_like_an_application_so_the_advance_VAT_is_accounted_for_exactly_once()
    {
        await using var h = await NewAsync();
        await AddIncomeAccountAsync(h);
        await SetAsync(h, "AdvancesAccount", "WhenReceived");
        var advance = (await AdvanceAsync(h, 119_000m, rate: 19m)).Value!;

        Assert.True((await KeepAsync(h, advance.Id, 33_333.33m)).Ok);
        Assert.True((await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(null, 40_000m), "x")).Ok);
        h.Db.ChangeTracker.Clear();
        Assert.True((await KeepAsync(h, advance.Id, null)).Ok);  // all that is left

        var rows = await h.Db.CustomerAdvanceApplications.AsNoTracking().ToListAsync();
        Assert.Equal(5_322.13m, rows.Single(a => a.Amount == 33_333.33m).TaxAmount);  // 19,000 × 33,333.33 / 119,000, rounded
        Assert.Equal(6_386.55m, rows.Single(a => a.Kind == AdvanceMoveKind.Application).TaxAmount);
        Assert.Equal((119_000m, 19_000m), (rows.Sum(a => a.Amount), rows.Sum(a => a.TaxAmount)));
        Assert.Empty(await CustomerAdvances.OpenAsync(h.Db, Contractor));

        // Kept parts' VAT stays due; the released part's is given back: net 4457 is exactly the kept parts' share.
        var keptTax = rows.Where(a => a.Kind == AdvanceMoveKind.Kept).Sum(a => a.TaxAmount);
        var posted = await h.PostedAsync();
        Assert.Equal(-keptTax, posted[LedgerAccounts.TaxCollected]);
        Assert.Equal(0m, await h.AccountAsync(LedgerAccounts.TaxCollected, Contractor));
        Assert.Equal(0m, posted.Values.Sum());
        var statement = (await StatementAsync(h))!;
        Assert.Equal((-40_000m, 0m), (statement.ClosingBalance, statement.BalanceDifference));
    }

    // ---------- a receipt's unallocated credit on 411 ----------

    [Fact]
    public async Task On_411_the_unallocated_part_of_a_receipt_is_kept_capped_by_it_and_by_the_customers_credit()
    {
        await using var h = await NewAsync();
        await AddIncomeAccountAsync(h);
        await SetAsync(h, "CustomerAccount");

        // 200,000 received, 142,800 of it against the invoice: 57,200 is a credit on 411.
        var invoice = (await h.RaiseAsync()).Value!;
        var receipt = (await h.ReceiveAsync(200_000m, invoiceId: invoice.Id, allocate: 142_800m)).Value!;
        Assert.Equal(-57_200m, await h.BalanceAsync());

        Assert.Equal(ReceivableFailure.Invalid, (await KeepAsync(h, receipt.Id, 57_200.01m)).Failure);
        var kept = await KeepAsync(h, receipt.Id, 20_000m, reason: "Acompte conservé — commande annulée");
        Assert.True(kept.Ok, kept.Message);
        var row = await h.Db.CustomerAdvanceApplications.AsNoTracking().SingleAsync();
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Customers + "/c"] = 20_000m, [OtherIncome + "/c"] = -20_000m },
            await EntryAsync(h, LedgerSources.CustomerAdvanceKept, row.Id));
        Assert.Equal((-37_200m, -37_200m), (await h.TradeDebtAsync(), await h.BalanceAsync()));

        // The customer's credit caps it too: a refund of 30,000 raised and not paid leaves 7,200 to keep.
        Assert.True((await Vouchers(h).CreateAsync(new CreatePaymentVoucherRequest(Algiers, PaymentVoucherKind.CustomerRefund, 30_000m, "cash", CustomerId: Contractor), "caissier")).Ok);
        h.Db.ChangeTracker.Clear();
        var capped = await KeepAsync(h, receipt.Id, 10_000m);
        Assert.Equal(ReceivableFailure.Invalid, capped.Failure);
        Assert.Contains("7200", capped.Message!.Replace(",", "").Replace(" ", ""));
        Assert.True((await KeepAsync(h, receipt.Id, null)).Ok);
        Assert.Equal(27_200m, (await h.Db.CustomerAdvanceApplications.AsNoTracking().ToListAsync()).Sum(a => a.Amount));
        Assert.Equal(ReceivableFailure.Conflict, (await KeepAsync(h, receipt.Id, null)).Failure);

        // The statement: the invoice, the receipt, two kept rows as debits; it closes on what the customer is still owed.
        var statement = (await StatementAsync(h))!;
        var keptRows = statement.Rows.Where(r => r.SourceType == LedgerSources.CustomerAdvanceKept).ToList();
        Assert.Equal(2, keptRows.Count);
        Assert.All(keptRows, r => Assert.Equal((receipt.Number, 0m), (r.DocumentNumber, r.Credit)));
        Assert.Equal(27_200m, keptRows.Sum(r => r.Debit));
        Assert.Equal((-30_000m, 0m), (statement.ClosingBalance, statement.BalanceDifference));
        Assert.Equal(0m, (await h.PostedAsync()).Values.Sum());
    }

    [Fact]
    public async Task A_pending_cheque_or_a_repair_receipt_is_never_kept()
    {
        await using var h = await NewAsync();
        await AddIncomeAccountAsync(h);
        await SetAsync(h, "CustomerAccount");
        var cheque = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 50_000m, "cheque", Reference: "CHQ-1", InstrumentNumber: "0001"), "caissier");
        Assert.True(cheque.Ok, cheque.Message);
        Assert.Equal(PaymentStatus.Pending, cheque.Value!.Status);
        Assert.Equal(ReceivableFailure.Conflict, (await KeepAsync(h, cheque.Value.Id, null)).Failure);
        Assert.Equal(ReceivableFailure.NotFound, (await KeepAsync(h, Guid.NewGuid(), null)).Failure);

        // The account chosen has since been switched off: refused with the settings' code.
        var account = await h.Db.LedgerAccounts.SingleAsync(a => a.Code == OtherIncome);
        account.IsActive = false;
        await h.Db.SaveChangesAsync();
        var receipt = (await h.ReceiveAsync(10_000m)).Value!;
        var refused = await KeepAsync(h, receipt.Id, null);
        Assert.Equal((ReceivableFailure.Conflict, ReceivableSettingsStore.AccountsNotMappedCode), (refused.Failure, refused.Code));
    }

    // ---------- over the route ----------

    [Fact]
    public async Task Over_the_route_keeping_is_refused_by_default_then_kept_and_the_voucher_says_so()
    {
        await using var h = await NewAsync();
        await AddIncomeAccountAsync(h);
        var admin = ParadisERP.Tests.Security.TillHarness.Principal(UserRole.Admin, null);
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        Task<(int Status, string Body)> Call(string method, string pattern, string json = "", params (string, string)[] route) =>
            ParadisERP.Tests.Security.BranchLockTests.InvokeAsync(h.Db, a => ParadisERP.API.Endpoints.ReceivableEndpoints.MapReceivableEndpoints(a),
                method, pattern, admin, json, route);

        var receipt = (await h.ReceiveAsync(80_000m)).Value!;
        h.Db.ChangeTracker.Clear();
        var refused = await Call("POST", "/api/receivables/receipts/{id:guid}/keep", """{"amount":1000,"reason":"Annulée"}""", ("id", receipt.Id.ToString()));
        Assert.Equal(400, refused.Status);
        Assert.Contains(ReceivableDocuments.KeptAdvancesNotAllowedCode, refused.Body);

        var set = await Call("PUT", "/api/receivables/settings",
            $$"""{"advancesHeldOn":"CustomerAccount","advanceVat":"AtInvoice","keptAdvances":"OtherIncome","keptIncomeAccount":"{{OtherIncome}}"}""");
        Assert.True(set.Status == 200, set.Body);
        Assert.Equal("OtherIncome", System.Text.Json.JsonSerializer.Deserialize<ReceivableSettingsDto>(set.Body, web)!.KeptAdvances);
        h.Db.ChangeTracker.Clear();

        var kept = await Call("POST", "/api/receivables/receipts/{id:guid}/keep", """{"amount":30000,"reason":"Commande annulée"}""", ("id", receipt.Id.ToString()));
        Assert.True(kept.Status == 200, kept.Body);
        var voucher = System.Text.Json.JsonSerializer.Deserialize<ReceiptVoucherDto>(kept.Body, web)!;
        Assert.Equal((30_000m, 50_000m), (voucher.Kept, voucher.Unallocated));
        var line = Assert.Single(voucher.AdvanceApplications!);
        Assert.Equal(("Kept", "Commande annulée", OtherIncome, 30_000m), (line.Kind, line.Reason, line.IncomeAccountCode, line.Amount));
    }

    private sealed class AlgeriaForTests : ParadisERP.Application.Regional.IRegionalSettingsProvider
    {
        public ParadisERP.Application.Regional.RegionalSettings Get() =>
            new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }
}
