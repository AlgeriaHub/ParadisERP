using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Receivables;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Receivables;
using ParadiERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Receivables;

/// <summary>
/// One statement for everything (owner, 2026-10-10): a customer who buys and rents, and pays for
/// both, sees sales, rentals, receipts and the refund paid out on one statement whose balance is
/// the account's to the last unit — and the admin may narrow it to sales or to rentals.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerStatementAllKindsTests
{
    private sealed class Algeria : ParadiERP.Application.Regional.IRegionalSettingsProvider
    {
        public ParadiERP.Application.Regional.RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    [Fact]
    public async Task Sales_rentals_receipts_and_a_refund_on_one_statement_that_ends_on_the_customer_s_balance()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var customer = ReceivableHarness.Contractor;

        // A sale paid in full at the till (it never touches 411) and one partly on account.
        var cashSale = await h.PushSaleAsync(1_000m, 1_000m);
        await h.PushSaleAsync(5_000m, 2_000m);

        // Goods out on hire, invoiced over the issue note.
        var contract = await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            customer, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 4, 10, RentUnitPrice: 250m, DamageUnitPrice: 2_000m)])), "admin");
        Assert.True(contract.Ok, contract.Message);
        var issued = await h.Rentals.IssueAsync(contract.Value!.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers, [new IssueNoteLineRequest(contract.Value.Lines.Single().Id, 4)],
            DateTime.UtcNow.AddDays(-1))), null, "magasinier");
        Assert.True(issued.Ok, issued.Message);
        var rent = issued.Value!.PeriodRent;
        var rentalInvoice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Value.Note.Id), "admin");
        Assert.True(rentalInvoice.Ok, rentalInvoice.Message);

        // A receipt allocated to the rental invoice, and one allocated to nothing.
        Assert.True((await h.ReceiveAsync(1_500m, invoiceId: rentalInvoice.Value!.Id)).Ok);
        Assert.True((await h.ReceiveAsync(20_000m)).Ok);

        // A refund paid back to the customer, the way it is paid: raised, approved by another, paid.
        var vouchers = new PaymentVouchers(h.Db, new LedgerPoster(h.Db), new Algeria());
        var raised = await vouchers.CreateAsync(new CreatePaymentVoucherRequest(
            ReceivableHarness.Algiers, PaymentVoucherKind.CustomerRefund, 500m, "cash", CustomerId: customer), "caissier");
        Assert.True(raised.Ok, raised.Message);
        Assert.True((await vouchers.ApproveAsync(raised.Value.Voucher.Id, null, new VoucherActor(null, "gerant", false))).Ok);
        var paid = await vouchers.PayAsync(raised.Value.Voucher.Id, null, new VoucherActor(null, "gerant", false));
        Assert.True(paid.Ok, paid.Message);

        var from = DateTime.UtcNow.AddDays(-5);
        var to = DateTime.UtcNow.AddDays(1);
        var all = (await h.Statements.BuildAsync(customer, from, to))!;
        var balance = (await h.Db.Customers.AsNoTracking().SingleAsync(c => c.Id == customer)).Balance;

        // The whole account: it ends where the customer's balance and account 411 stand.
        Assert.Equal("all", all.Filter);
        Assert.Equal(3_000m + rent - 1_500m - 20_000m + 500m, all.ClosingBalance);
        Assert.Equal(balance, all.ClosingBalance + all.PendingNotes);
        Assert.Equal(0m, all.BalanceDifference);
        Assert.Equal(all.ClosingBalance, all.Rows[^1].Balance);
        Assert.Equal(all.TotalDebit - all.TotalCredit, all.ClosingBalance - all.OpeningBalance);

        // The cash sale is on it, as sold and paid at once, and moves nothing.
        var cash = Assert.Single(all.Rows, r => r.SourceId == cashSale);
        Assert.Equal((CustomerStatementKinds.Sale, 1_000m, 1_000m, 1_000m), (cash.Kind, cash.Debit, cash.Credit, cash.PaidAtTill));
        Assert.Equal(
            new[] { CustomerStatementKinds.Payment, CustomerStatementKinds.Receipt, CustomerStatementKinds.Rental, CustomerStatementKinds.Sale },
            all.Rows.Select(r => r.Kind).Distinct().Order().ToArray());

        // Narrowed: the sales, and the rental with the part of the receipt that paid it.
        var sales = (await h.Statements.BuildAsync(customer, from, to, filter: StatementFilter.Sales))!;
        Assert.Equal("sales", sales.Filter);
        Assert.All(sales.Rows, r => Assert.Equal(CustomerStatementKinds.Sale, r.Kind));
        Assert.Equal(3_000m, sales.ClosingBalance);

        var rentals = (await h.Statements.BuildAsync(customer, from, to, filter: StatementFilter.Rentals))!;
        Assert.Equal(rent - 1_500m, rentals.ClosingBalance);
        Assert.Contains(rentals.Rows, r => r.Kind == CustomerStatementKinds.Receipt && r.Credit == 1_500m);
        Assert.DoesNotContain(rentals.Rows, r => r.Kind == CustomerStatementKinds.Sale);

        // What belongs to neither — the unallocated receipt and the refund — is the difference.
        Assert.Equal(all.ClosingBalance, sales.ClosingBalance + rentals.ClosingBalance - 20_000m + 500m);
        // The reconciliation figures are the whole account's whatever is shown.
        Assert.Equal(all.TotalOwed, sales.TotalOwed);
        Assert.Equal(0m, sales.BalanceDifference);
    }

    [Fact]
    public async Task A_receipt_spread_over_a_sale_and_a_rental_counts_in_each_by_its_allocation()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var saleInvoice = await h.RaiseAsync(qty: 1, unitPrice: 1_000m, taxRate: 0m);
        Assert.True(saleInvoice.Ok, saleInvoice.Message);
        var contract = await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 2, 10, RentUnitPrice: 300m, DamageUnitPrice: 2_000m)])), "admin");
        var issued = await h.Rentals.IssueAsync(contract.Value!.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers, [new IssueNoteLineRequest(contract.Value.Lines.Single().Id, 2)],
            DateTime.UtcNow.AddDays(-1))), null, "magasinier");
        Assert.True(issued.Ok, issued.Message);
        var rentalInvoice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Value!.Note.Id), "admin");
        Assert.True(rentalInvoice.Ok, rentalInvoice.Message);

        var receipt = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers, 1_000m, "cash",
            Allocations: [new ReceiptAllocationRequest(saleInvoice.Value!.Id, 400m), new ReceiptAllocationRequest(rentalInvoice.Value!.Id, 350m)]), "caissier");
        Assert.True(receipt.Ok, receipt.Message);

        var from = DateTime.UtcNow.AddDays(-5);
        var to = DateTime.UtcNow.AddDays(1);
        var sales = (await h.Statements.BuildAsync(ReceivableHarness.Contractor, from, to, filter: StatementFilter.Sales))!;
        var rentals = (await h.Statements.BuildAsync(ReceivableHarness.Contractor, from, to, filter: StatementFilter.Rentals))!;
        var all = (await h.Statements.BuildAsync(ReceivableHarness.Contractor, from, to))!;

        Assert.Equal(400m, Assert.Single(sales.Rows, r => r.Kind == CustomerStatementKinds.Receipt).Credit);
        Assert.Equal(350m, Assert.Single(rentals.Rows, r => r.Kind == CustomerStatementKinds.Receipt).Credit);
        Assert.Equal(1_000m, Assert.Single(all.Rows, r => r.Kind == CustomerStatementKinds.Receipt).Credit);
        // 250 of the receipt was allocated to nothing: it is in the whole account only.
        Assert.Equal(all.ClosingBalance, sales.ClosingBalance + rentals.ClosingBalance - 250m);
    }

    [Theory]
    [InlineData(null, StatementFilter.All)]
    [InlineData("", StatementFilter.All)]
    [InlineData("ALL", StatementFilter.All)]
    [InlineData("sales", StatementFilter.Sales)]
    [InlineData(" Rentals ", StatementFilter.Rentals)]
    public void A_filter_is_read_whatever_its_case(string? value, StatementFilter expected) =>
        Assert.Equal(expected, StatementFilters.Parse(value));

    [Fact]
    public void Anything_else_is_not_a_filter() => Assert.Null(StatementFilters.Parse("purchases"));

    // ---------- the narrowed view is the admin's, decided by the server ----------

    private const string Password = "Essai@2026";

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode = "C01" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }

    private static string[] Capabilities(JsonElement login) =>
        login.GetProperty("user").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToArray();

    [Fact]
    public async Task Only_an_account_holding_the_filter_permission_may_narrow_a_statement()
    {
        using var host = new PosHost();
        var (admin, adminLogin) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using var _ = admin;
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var branch = branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
        var created = await admin.PostAsJsonAsync("/api/catalog/customers", new
        {
            code = "STMT-01", fullName = "Client relevé", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = new DateTime(1980, 3, 4), creditLimit = 0m, kind = "Person",
            idDocumentNumber = "109 876 544",
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var customer = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await TestUsers.CreateAsync(host, admin, "gerant-releve", "Manager", branch, Password);
        var (manager, managerLogin) = await SignedInAsync(host, host.SignInName("gerant-releve"), Password);
        using var __ = manager;

        // The UI reads the capability from the login; the server holds the decision.
        Assert.Contains(ParadiERP.Domain.Capabilities.FilterCustomerStatements, Capabilities(adminLogin));
        Assert.DoesNotContain(ParadiERP.Domain.Capabilities.FilterCustomerStatements, Capabilities(managerLogin));

        var url = $"/api/receivables/statement?customerId={customer}";
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url + "&kind=all")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url + "&kind=sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url + "&kind=rentals")).StatusCode);

        var narrowed = await admin.GetAsync(url + "&kind=rentals");
        Assert.Equal(HttpStatusCode.OK, narrowed.StatusCode);
        Assert.Equal("rentals", (await narrowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("filter").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(url + "&kind=purchases")).StatusCode);
    }
}
