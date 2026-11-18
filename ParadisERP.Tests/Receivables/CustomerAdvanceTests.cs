using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Receivables.ReceivableHarness;

namespace ParadisERP.Tests.Receivables;

/// <summary>
/// M7b (SAL-12, D45, docs/accounting/advances.md): where a customer advance is held and when its VAT is due, as the
/// accountant's settings. The default (411, VAT at the invoice) posts exactly what a receipt always has; with 419 an advance
/// waits there until it is applied to an invoice (Dr 419 / Cr 411); with VAT when received its tax is declared on receipt
/// and given back when it is applied, so the invoice's VAT is never counted twice. The customer's statement and balance
/// read the same either way.
/// </summary>
public class CustomerAdvanceTests
{
    private static async Task SetAsync(ReceivableHarness h, string heldOn, string vat = "AtInvoice")
    {
        var saved = await new ReceivableSettingsStore(h.Db).UpdateAsync(new UpdateReceivableSettingsRequest(heldOn, vat), "comptable");
        Assert.True(saved.Saved is not null, saved.Error);
        h.Db.ChangeTracker.Clear();
    }

    private static Task<ReceivableResult<ReceiptVoucher>> AdvanceAsync(
        ReceivableHarness h, decimal amount, decimal? rate = null, string method = "cash") =>
        h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, amount, method, Reference: method == "cash" ? null : "CHQ-1",
            InstrumentNumber: method == "cash" ? null : "0001", Kind: "advance", AdvanceTaxRate: rate), "caissier");

    /// <summary>What one entry source posted, by account (all of them when no id).</summary>
    private static async Task<Dictionary<string, decimal>> PostedAsync(ReceivableHarness h, string source, Guid? id = null)
    {
        var accounts = await h.Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var entries = await h.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.SourceType == source && (id == null || e.SourceId == id)).ToListAsync();
        return entries.SelectMany(e => e.Lines).GroupBy(l => accounts[l.AccountId]).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    private static Task<CustomerStatementDto?> StatementAsync(ReceivableHarness h) =>
        h.Statements.BuildAsync(Contractor, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

    // ---------- the default: exactly today ----------

    [Fact]
    public async Task By_default_an_advance_is_an_unallocated_receipt_on_411_with_no_VAT_and_nothing_reaches_419()
    {
        await using var h = await NewAsync();
        var settings = await new ReceivableSettingsStore(h.Db).SettingsAsync();
        Assert.Equal(("CustomerAccount", "AtInvoice", true, 0m), (settings.AdvancesHeldOn, settings.AdvanceVat, settings.AdvancesAccountReady, settings.OpenAdvances));
        Assert.Equal(0, await h.Db.ReceivableSettings.CountAsync());
        Assert.All(typeof(ReceivableSettings).GetProperties().Where(p => p.PropertyType.IsEnum),
            p => Assert.Equal(0, Convert.ToInt32(p.GetValue(new ReceivableSettings()))));

        // The money taken before anything is invoiced: a payment allocated to nothing, Dr 530 / Cr 411, as always.
        var receipt = await h.ReceiveAsync(100_000m);
        Assert.True(receipt.Ok, receipt.Message);
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Cash] = 100_000m, [LedgerAccounts.Customers] = -100_000m },
            await PostedAsync(h, LedgerSources.CustomerReceipt));
        Assert.Equal((ReceiptKind.Payment, 0m, 0m, (Guid?)null), (receipt.Value!.Kind, receipt.Value.AdvanceTaxRate, receipt.Value.AdvanceTaxAmount, receipt.Value.MaintenanceJobId));

        // An advance kind, its VAT or a job is refused while advances are held on 411.
        Assert.Contains("411", (await AdvanceAsync(h, 10_000m)).Message);
        Assert.Contains("Only an advance", (await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 10_000m, "cash", AdvanceTaxRate: 19m), "caissier")).Message);

        // The invoice is posted as ever, the customer pays it off the account as ever, and 419 never moves.
        var invoice = (await h.RaiseAsync()).Value!;
        Assert.Equal(42_800m, await h.TradeDebtAsync());
        Assert.False(await h.Db.LedgerEntryLines.AnyAsync(l => l.Account!.Code == LedgerAccounts.CustomerDeposits));
        Assert.False(await h.Db.LedgerEntries.AnyAsync(e => e.SourceType == LedgerSources.CustomerAdvanceApplication));
        Assert.Equal(142_800m, await h.OutstandingAsync(invoice.Id));

        var statement = (await StatementAsync(h))!;
        Assert.Equal((42_800m, 0m), (statement.ClosingBalance, statement.BalanceDifference));
        Assert.Equal(-100_000m, statement.UninvoicedBalance);
    }

    // ---------- the settings ----------

    [Fact]
    public async Task VAT_on_receipt_needs_419_419_needs_its_account_and_411_waits_for_MS_35()
    {
        await using var h = await NewAsync();
        var store = new ReceivableSettingsStore(h.Db);

        var noVatOn411 = await store.UpdateAsync(new("CustomerAccount", "WhenReceived"), "comptable");
        Assert.Equal((400, ReceivableSettingsStore.InvalidCode), (noVatOn411.Status, noVatOn411.Code));
        Assert.Equal(400, (await store.UpdateAsync(new("Somewhere", "AtInvoice"), "comptable")).Status);

        var deposits = await h.Db.LedgerAccounts.SingleAsync(a => a.Code == LedgerAccounts.CustomerDeposits);
        deposits.IsActive = false;
        await h.Db.SaveChangesAsync();
        Assert.False((await store.SettingsAsync()).AdvancesAccountReady);
        var unmapped = await store.UpdateAsync(new("AdvancesAccount", "AtInvoice"), "comptable");
        Assert.Equal((409, ReceivableSettingsStore.AccountsNotMappedCode), (unmapped.Status, unmapped.Code));
        Assert.Equal(0, await h.Db.ReceivableSettings.CountAsync());

        deposits.IsActive = true;
        await h.Db.SaveChangesAsync();
        var on = (await store.UpdateAsync(new("AdvancesAccount", "WhenReceived"), "comptable")).Saved!;
        Assert.Equal(("AdvancesAccount", "WhenReceived", "comptable"), (on.AdvancesHeldOn, on.AdvanceVat, on.UpdatedBy));

        // Maintenance advances taken on 419 (MS-35) keep it there.
        h.Db.MaintenanceSettings.Add(new MaintenanceSettings { Advances = MaintenanceAdvances.Taken });
        await h.Db.SaveChangesAsync();
        var back = await store.UpdateAsync(new("CustomerAccount", "AtInvoice"), "comptable");
        Assert.Equal((409, ReceivableSettingsStore.MaintenanceAdvancesCode), (back.Status, back.Code));
        Assert.True(await ReceivableSettingsStore.AdvancesOn419Async(h.Db));
    }

    // ---------- 419 ----------

    [Fact]
    public async Task On_419_an_advance_waits_there_and_its_application_moves_it_to_411_against_the_invoice()
    {
        await using var h = await NewAsync();
        await SetAsync(h, "AdvancesAccount");

        var taken = await AdvanceAsync(h, 100_000m);
        Assert.True(taken.Ok, taken.Message);
        var advance = taken.Value!;
        Assert.Equal((ReceiptKind.Advance, "Acompte", 0m), (advance.Kind, advance.Purpose, advance.AdvanceTaxAmount));
        // Dr 530 / Cr 419, the customer's; nothing on 411; the money is still the customer's with us.
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Cash] = 100_000m, [LedgerAccounts.CustomerDeposits] = -100_000m },
            await PostedAsync(h, LedgerSources.CustomerReceipt, advance.Id));
        Assert.Equal((-100_000m, 0m), (await h.AccountAsync(LedgerAccounts.CustomerDeposits, Contractor), await h.TradeDebtAsync()));
        Assert.Equal(-100_000m, await h.BalanceAsync());

        // The statement reads 411 and 419 together: one credit row of the whole sum, no drift; the ageing agrees.
        var before = (await StatementAsync(h))!;
        Assert.Equal((-100_000m, 0m), (before.ClosingBalance, before.BalanceDifference));
        Assert.Equal(100_000m, before.Rows.Single().Credit);
        Assert.Equal(-100_000m, (await h.Statements.AgeAsync(DateTime.UtcNow.AddMinutes(1), Contractor)).Customers.Single().LedgerBalance);

        // An advance takes no invoice when it is taken.
        var invoice = (await h.RaiseAsync()).Value!;  // 120,000 + 19 % = 142,800
        Assert.Contains("apply it", (await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 1_000m, "cash", Allocations: [new ReceiptAllocationRequest(invoice.Id, 1_000m)], Kind: "advance"), "x")).Message);

        // Applied with no amount: all of it, no more than the invoice owes. Dr 419 / Cr 411, the customer's balance unmoved.
        var applied = await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(invoice.Id), "caissier");
        Assert.True(applied.Ok, applied.Message);
        var application = await h.Db.CustomerAdvanceApplications.AsNoTracking().SingleAsync();
        Assert.Equal((invoice.Id, 100_000m, 0m), (application.InvoiceId, application.Amount, application.TaxAmount));
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.CustomerDeposits] = 100_000m, [LedgerAccounts.Customers] = -100_000m },
            await PostedAsync(h, LedgerSources.CustomerAdvanceApplication, application.Id));
        Assert.Equal((0m, 42_800m, 42_800m), (await h.AccountAsync(LedgerAccounts.CustomerDeposits, Contractor), await h.TradeDebtAsync(), await h.OutstandingAsync(invoice.Id)));
        Assert.Equal(42_800m, await h.BalanceAsync());
        Assert.Equal(100_000m, (await h.Db.ReceiptAllocations.AsNoTracking().SingleAsync(a => a.ReceiptVoucherId == advance.Id)).Amount);

        // The statement: the invoice, the advance, no row for the application (it nets to nothing), no drift.
        var after = (await StatementAsync(h))!;
        Assert.Equal((42_800m, 0m, 0m), (after.ClosingBalance, after.BalanceDifference, after.UninvoicedBalance));
        Assert.Equal(2, after.Rows.Count);
        Assert.DoesNotContain(after.Rows, r => r.SourceType == LedgerSources.CustomerAdvanceApplication);

        // Nothing left to apply.
        Assert.Equal(ReceivableFailure.Conflict, (await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(invoice.Id), "x")).Failure);
        Assert.Empty(await CustomerAdvances.OpenAsync(h.Db, Contractor));
    }

    [Fact]
    public async Task Part_of_an_advance_pays_an_invoice_the_rest_is_released_for_a_refund_and_the_ledger_stays_square()
    {
        await using var h = await NewAsync();
        await SetAsync(h, "AdvancesAccount");
        var advance = (await AdvanceAsync(h, 200_000m)).Value!;
        var invoice = (await h.RaiseAsync()).Value!;

        // More than the invoice owes, or than the advance holds, is refused; another customer's invoice too.
        Assert.Equal(ReceivableFailure.Invalid, (await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(invoice.Id, 150_000m), "x")).Failure);
        Assert.Equal(ReceivableFailure.Invalid, (await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(null, 250_000m), "x")).Failure);
        var payment = (await h.ReceiveAsync(5_000m)).Value!;
        Assert.Equal(ReceivableFailure.Invalid, (await h.Receivables.ApplyAdvanceAsync(payment.Id, new ApplyAdvanceRequest(invoice.Id), "x")).Failure);

        Assert.True((await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(invoice.Id, 142_800m), "x")).Ok);
        Assert.Equal(0m, await h.OutstandingAsync(invoice.Id));
        Assert.Equal(57_200m, (await CustomerAdvances.OpenAsync(h.Db, Contractor))[advance.Id]);

        // A refund cannot debit 411 for money still on 419: the advance is released to the account first.
        var vouchers = new PaymentVouchers(h.Db, new LedgerPoster(h.Db), new AlgeriaForTests());
        var refund = new CreatePaymentVoucherRequest(Algiers, PaymentVoucherKind.CustomerRefund, 20_000m, "cash", CustomerId: Contractor);
        var refused = await vouchers.CreateAsync(refund, "caissier");
        Assert.Equal(ReceivableFailure.Conflict, refused.Failure);
        Assert.Contains("5", refused.Message);  // only the 5,000 paid on 411 is there

        var released = await h.Receivables.ApplyAdvanceAsync(advance.Id, new ApplyAdvanceRequest(null, 20_000m), "x");
        Assert.True(released.Ok, released.Message);
        Assert.Null((await h.Db.CustomerAdvanceApplications.AsNoTracking().OrderBy(a => a.AppliedAtUtc).LastAsync()).InvoiceId);
        Assert.True((await vouchers.CreateAsync(refund, "caissier")).Ok);

        // 419 holds what is still open; 411 + 419 is the customer's balance; every entry balanced.
        Assert.Equal(-37_200m, await h.AccountAsync(LedgerAccounts.CustomerDeposits, Contractor));
        Assert.Equal(-25_000m, await h.TradeDebtAsync());  // the 5,000 payment and the 20,000 released
        Assert.Equal(-62_200m, await h.BalanceAsync());
        var statement = (await StatementAsync(h))!;
        Assert.Equal((-62_200m, 0m), (statement.ClosingBalance, statement.BalanceDifference));
        Assert.Equal(0m, (await h.PostedAsync()).Values.Sum());
    }

    [Fact]
    public async Task A_pending_cheque_advance_posts_nothing_and_cannot_be_applied_until_it_clears()
    {
        await using var h = await NewAsync();
        await SetAsync(h, "AdvancesAccount");
        var cheque = (await AdvanceAsync(h, 50_000m, method: "cheque")).Value!;
        Assert.Equal(PaymentStatus.Pending, cheque.Status);
        Assert.Empty(await PostedAsync(h, LedgerSources.CustomerReceipt));
        var invoice = (await h.RaiseAsync()).Value!;
        Assert.Equal(ReceivableFailure.Conflict, (await h.Receivables.ApplyAdvanceAsync(cheque.Id, new ApplyAdvanceRequest(invoice.Id), "x")).Failure);

        Assert.True((await h.Receivables.SettleReceiptAsync(cheque.Id, "Confirmed", null, "chef")).Ok);
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Bank] = 50_000m, [LedgerAccounts.CustomerDeposits] = -50_000m },
            await PostedAsync(h, LedgerSources.CustomerReceipt));
        Assert.True((await h.Receivables.ApplyAdvanceAsync(cheque.Id, new ApplyAdvanceRequest(invoice.Id), "x")).Ok);
        Assert.Equal(92_800m, await h.OutstandingAsync(invoice.Id));
    }

    // ---------- VAT when the advance is received (D45) ----------

    [Fact]
    public async Task With_VAT_when_received_a_taxed_advance_declares_its_VAT_on_receipt_and_the_invoice_does_not_count_it_twice()
    {
        await using var h = await NewAsync();
        await SetAsync(h, "AdvancesAccount", "WhenReceived");

        // Untaxed unless the seller ticks it (VAT only on request): no 4457 line at all.
        var plain = (await AdvanceAsync(h, 10_000m)).Value!;
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Cash] = 10_000m, [LedgerAccounts.CustomerDeposits] = -10_000m },
            await PostedAsync(h, LedgerSources.CustomerReceipt, plain.Id));

        // 119,000 taken at 19 %: 19,000 of it is VAT, declared now; 100,000 waits on 419. Both lines the customer's.
        var taxed = (await AdvanceAsync(h, 119_000m, rate: 19m)).Value!;
        Assert.Equal((19m, 19_000m), (taxed.AdvanceTaxRate, taxed.AdvanceTaxAmount));
        Assert.Equal(new Dictionary<string, decimal>
        {
            [LedgerAccounts.Cash] = 119_000m, [LedgerAccounts.CustomerDeposits] = -100_000m, [LedgerAccounts.TaxCollected] = -19_000m,
        }, await PostedAsync(h, LedgerSources.CustomerReceipt, taxed.Id));
        Assert.Equal(-19_000m, await h.AccountAsync(LedgerAccounts.TaxCollected, Contractor));

        // The statement still reads the whole sum the customer handed over.
        var before = (await StatementAsync(h))!;
        Assert.Equal((-129_000m, 0m), (before.ClosingBalance, before.BalanceDifference));
        Assert.Contains(before.Rows, r => r.Credit == 119_000m);

        // The invoice declares its whole VAT (22,800) as always; applying the advance gives back the 19,000 declared on
        // receipt, so the VAT on the books is the invoice's, once.
        var invoice = (await h.RaiseAsync()).Value!;
        Assert.Equal(22_800m, invoice.TaxTotal);
        var half = await h.Receivables.ApplyAdvanceAsync(taxed.Id, new ApplyAdvanceRequest(invoice.Id, 59_500m), "x");
        Assert.True(half.Ok, half.Message);
        var first = await h.Db.CustomerAdvanceApplications.AsNoTracking().SingleAsync();
        Assert.Equal(new Dictionary<string, decimal>
        {
            [LedgerAccounts.CustomerDeposits] = 50_000m, [LedgerAccounts.TaxCollected] = 9_500m, [LedgerAccounts.Customers] = -59_500m,
        }, await PostedAsync(h, LedgerSources.CustomerAdvanceApplication, first.Id));
        Assert.True((await h.Receivables.ApplyAdvanceAsync(taxed.Id, new ApplyAdvanceRequest(invoice.Id), "x")).Ok);

        var vat = (await h.PostedAsync())[LedgerAccounts.TaxCollected];
        Assert.Equal(-22_800m, vat);
        Assert.Equal((0m, 0m), (await h.AccountAsync(LedgerAccounts.TaxCollected, Contractor), await h.AccountAsync(LedgerAccounts.CustomerDeposits, Contractor) + 10_000m));
        Assert.Equal(23_800m, await h.OutstandingAsync(invoice.Id));
        var after = (await StatementAsync(h))!;
        Assert.Equal((13_800m, 0m), (after.ClosingBalance, after.BalanceDifference));
        Assert.Equal(0m, (await h.PostedAsync()).Values.Sum());
    }

    [Fact]
    public async Task A_taxed_advance_is_refused_while_VAT_waits_for_the_invoice()
    {
        await using var h = await NewAsync();
        await SetAsync(h, "AdvancesAccount");
        var refused = await AdvanceAsync(h, 119_000m, rate: 19m);
        Assert.Equal(ReceivableFailure.Invalid, refused.Failure);
        Assert.Contains("at the invoice", refused.Message);
        Assert.Equal(0, await h.Db.ReceiptVouchers.CountAsync());
    }

    [Fact]
    public async Task Over_the_routes_the_settings_are_set_an_advance_taken_and_applied_and_the_voucher_says_what_is_left()
    {
        await using var h = await NewAsync();
        var admin = ParadisERP.Tests.Security.TillHarness.Principal(UserRole.Admin, null);
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        Task<(int Status, string Body)> Call(string method, string pattern, string json = "", params (string, string)[] route) =>
            ParadisERP.Tests.Security.BranchLockTests.InvokeAsync(h.Db, a => ParadisERP.API.Endpoints.ReceivableEndpoints.MapReceivableEndpoints(a),
                method, pattern, admin, json, route);

        var (status, body) = await Call("GET", "/api/receivables/settings");
        Assert.Equal(200, status);
        Assert.Equal("CustomerAccount", System.Text.Json.JsonSerializer.Deserialize<ReceivableSettingsDto>(body, web)!.AdvancesHeldOn);
        var refused = await Call("PUT", "/api/receivables/settings", """{"advancesHeldOn":"CustomerAccount","advanceVat":"WhenReceived"}""");
        Assert.Equal(400, refused.Status);
        Assert.Equal(200, (await Call("PUT", "/api/receivables/settings", """{"advancesHeldOn":"AdvancesAccount","advanceVat":"WhenReceived"}""")).Status);
        h.Db.ChangeTracker.Clear();

        var taken = await Call("POST", "/api/receivables/receipts",
            $$"""{"customerId":"{{Contractor}}","branchId":"{{Algiers}}","amount":119000,"methodKey":"cash","kind":"advance","advanceTaxRate":19}""");
        Assert.True(taken.Status == 201, taken.Body);
        var advance = System.Text.Json.JsonSerializer.Deserialize<ReceiptVoucherDto>(taken.Body, web)!;
        Assert.Equal(("Advance", 19_000m, 119_000m), (advance.Kind, advance.AdvanceTaxAmount, advance.AdvanceOpen));
        h.Db.ChangeTracker.Clear();

        var invoice = (await h.RaiseAsync()).Value!;
        h.Db.ChangeTracker.Clear();
        var applied = await Call("POST", "/api/receivables/receipts/{id:guid}/apply",
            $$"""{"invoiceId":"{{invoice.Id}}","amount":100000}""", ("id", advance.Id.ToString()));
        Assert.True(applied.Status == 200, applied.Body);
        var after = System.Text.Json.JsonSerializer.Deserialize<ReceiptVoucherDto>(applied.Body, web)!;
        Assert.Equal(19_000m, after.AdvanceOpen);
        var line = Assert.Single(after.AdvanceApplications!);
        Assert.Equal((invoice.Number, 100_000m, 15_966.39m), (line.InvoiceNumber, line.Amount, line.TaxAmount));
    }

    private sealed class AlgeriaForTests : ParadisERP.Application.Regional.IRegionalSettingsProvider
    {
        public ParadisERP.Application.Regional.RegionalSettings Get() =>
            new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }
}
