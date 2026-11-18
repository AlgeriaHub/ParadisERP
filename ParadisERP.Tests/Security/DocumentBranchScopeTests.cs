using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Security;

/// <summary>
/// P0.1b (owner, 2026-10-25): a user bound to a branch — the Manager, a role the Admin made with the
/// assigned branch's scope, a cashier — reaches their own branch's documents only. Another branch's
/// contract and what hangs off it, invoice, credit note, receipt, voucher, sale and journal entry,
/// named by id in the route or in the body, are answered exactly as an id nobody holds: same status,
/// same body, no file. The copies of identity documents need <c>customers.documents.view</c> on top,
/// and the contract says nothing about them without it. Company-scoped roles read every branch. A
/// branch holding only part of an inter-branch journal entry still opens it from its statement, with
/// the other branches' lines folded into their totals.
/// </summary>
[Collection(HostCollection.Name)]
public class DocumentBranchScopeTests
{
    private const string Password = "Essai@2026";

    /// <summary>What a branch holds: one of everything a by-id route can name.</summary>
    private sealed record Docs(
        Guid Contract, Guid ContractLine, Guid Attachment, Guid IssueNote, Guid ReturnNote,
        Guid Invoice, Guid CreditNote, Guid Receipt, Guid Voucher, Guid Sale, Guid Entry);

    /// <summary>
    /// Alger (the main branch) and Oran, each with a contract carrying an identity copy, an issue note
    /// invoiced and partly credited, a return note with a damage charge, an extension, a receipt, an
    /// expense voucher, a till sale and the journal entries all of these post. One customer, with an
    /// identity copy of their own. Logins: the Admin and a Manager of Alger.
    /// </summary>
    private sealed record Scenario(
        PosHost Host, HttpClient Admin, HttpClient Manager,
        Guid Main, Guid Oran, Guid Customer, Guid CustomerScan, Docs Alger, Docs Orn);

    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
         0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9, 0x50, 0x31, 0x62, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static async Task<Scenario> SetUpAsync(PosHost host)
    {
        var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "C01");
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var main = branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
        var oran = await TestBranches.SellingAsync(admin, "Oran");

        var customer = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/customers", new
        {
            code = "IDOR-01", fullName = "Client partagé", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = new DateTime(1980, 3, 4), creditLimit = 0m, kind = "Person",
            idDocumentNumber = "109 876 543",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var site = (await JsonAsync(await admin.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "Chantier" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var customerScan = (await JsonAsync(await admin.PostAsync($"/api/catalog/customers/{customer}/attachments",
            Upload(Jpeg, "carte-client.jpg")), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var product = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "IDOR-ECH", nameAr = "سقالة", nameFr = "Échafaudage", nameEn = "Scaffold",
            price = 900m, costPrice = 0m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        await TestUsers.CreateAsync(host, admin, "caisse-alger", "Cashier", main, Password);
        await TestUsers.CreateAsync(host, admin, "caisse-oran", "Cashier", oran, Password);
        await TestUsers.CreateAsync(host, admin, "gerant-alger", "Manager", main, Password);

        var alger = await BranchDocsAsync(host, admin, main, customer, site, product, "caisse-alger");
        var orn = await BranchDocsAsync(host, admin, oran, customer, site, product, "caisse-oran");

        return new Scenario(host, admin, await SignedInAsync(host, "gerant-alger", Password, "C01"),
            main, oran, customer, customerScan, alger, orn);
    }

    private static async Task<Docs> BranchDocsAsync(
        PosHost host, HttpClient admin, Guid branch, Guid customer, Guid site, Guid product, string cashier)
    {
        await JsonAsync(await admin.PostAsJsonAsync($"/api/catalog/products/{product}/stock",
            new { branchId = branch, qtyDelta = 100m, reason = "opening", reorderLevel = (decimal?)null }), HttpStatusCode.OK);

        var contract = await JsonAsync(await admin.PostAsJsonAsync("/api/rentals/contracts", new
        {
            customerId = customer, branchId = branch, siteId = site,
            lines = new[] { new { productId = product, qty = 20m, durationDays = 30, rentUnitPrice = 50m, damageUnitPrice = 900m } },
        }), HttpStatusCode.Created);
        var contractId = contract.GetProperty("id").GetGuid();
        var line = contract.GetProperty("lines")[0].GetProperty("id").GetGuid();

        var scan = Upload(Jpeg, "carte.jpg");
        scan.Add(new StringContent("idDocument"), "kind");
        var attachment = (await JsonAsync(await admin.PostAsync($"/api/rentals/contracts/{contractId}/attachments", scan),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var issue = (await JsonAsync(await admin.PostAsJsonAsync($"/api/rentals/contracts/{contractId}/issue-notes", new
        {
            branchId = branch, customerTransport = true, lines = new[] { new { contractLineId = line, qty = 20m } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var issued = await JsonAsync(await admin.PostAsJsonAsync("/api/receivables/invoices",
            new { dueDateUtc = DateTime.UtcNow.AddDays(30), issueNoteId = issue }), HttpStatusCode.Created);
        var invoice = issued.GetProperty("invoice").GetProperty("id").GetGuid();
        var invoiceLine = issued.GetProperty("invoice").GetProperty("lines")[0].GetProperty("id").GetGuid();
        var credit = (await JsonAsync(await admin.PostAsJsonAsync($"/api/receivables/invoices/{invoice}/credit-notes",
            new { reason = "Une unité facturée en trop", lines = new[] { new { invoiceLineId = invoiceLine, qty = 1m } } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var returned = (await JsonAsync(await admin.PostAsJsonAsync($"/api/rentals/contracts/{contractId}/return-notes", new
        {
            branchId = branch, customerTransport = true,
            lines = new[] { new { contractLineId = line, qtyOk = 4m, qtyTotalDamage = 1m, sameDayUsage = "Used" } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await admin.PostAsJsonAsync($"/api/rentals/contracts/{contractId}/extensions",
            new { extraDays = 5, unitPrice = 50m, reason = "Prolongation" }), HttpStatusCode.Created);

        var receipt = (await JsonAsync(await admin.PostAsJsonAsync("/api/receivables/receipts", new
        {
            customerId = customer, branchId = branch, amount = 100m, methodKey = "card", reference = "TPE-1",
            allocations = new[] { new { invoiceId = invoice, amount = 100m } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var voucher = (await JsonAsync(await admin.PostAsJsonAsync("/api/payment-vouchers", new
        {
            branchId = branch, kind = (int)PaymentVoucherKind.Expense, amount = 300m, methodKey = "cash",
            payeeName = "Naftal", expenseCategory = "fuel",
        }), HttpStatusCode.Created)).GetProperty("voucher").GetProperty("id").GetGuid();

        var sale = await TillSaleAsync(host, cashier);
        var entries = await admin.GetFromJsonAsync<JsonElement>($"/api/ledger/entries?branchId={branch}&take=1000");
        var entry = entries.EnumerateArray().First(e => e.GetProperty("sourceId").GetGuid() == issue).GetProperty("id").GetGuid();

        return new Docs(contractId, line, attachment, issue, returned, invoice, credit, receipt, voucher, sale, entry);
    }

    /// <summary>One sale pushed from the cashier's own till, so it is booked at the till's branch.</summary>
    private static async Task<Guid> TillSaleAsync(PosHost host, string cashier)
    {
        var (till, session) = await SignedInWithBodyAsync(host, cashier, Password, null);
        using (till)
        {
            var code = session.GetProperty("deviceCode").GetString()!;
            var user = session.GetProperty("user").GetProperty("id").GetGuid();
            var id = Guid.NewGuid();
            var push = await till.PostAsJsonAsync("/api/sync/push", new
            {
                deviceCode = code,
                sales = new[]
                {
                    new
                    {
                        id, reference = $"{code}-000001", localSeq = 1L, soldAtUtc = DateTime.UtcNow, customerId = (Guid?)null,
                        cashierUserId = user, cashierName = cashier, subTotalHt = 10m, taxTotal = 0m, discountTotal = 0m,
                        grandTotalTtc = 10m, roundingAdjustment = 0m, paidAmount = 10m, changeAmount = 0m,
                        paymentMethod = "Cash", status = "Completed", refundOfSaleId = (Guid?)null, note = (string?)null, taxMode = "Untaxed",
                        lines = new[] { new { id = Guid.NewGuid(), productId = Guid.NewGuid(), sku = "X", nameSnapshot = "X", qty = 1m,
                            unitPrice = 10m, taxRate = 0m, lineDiscount = 0m, lineTotalTtc = 10m, lineTaxAmount = 0m } },
                        payments = new[] { new { id = Guid.NewGuid(), method = "Cash", amount = 10m, reference = (string?)null } },
                    },
                },
            });
            Assert.Equal(1, (await push.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
            return id;
        }
    }

    /// <summary>Every read by id P0.1b guards, for one branch's documents.</summary>
    private static IEnumerable<string> ReadsOf(Docs d) =>
    [
        $"/api/rentals/contracts/{d.Contract}",
        $"/api/rentals/contracts/{d.Contract}/settlement",
        $"/api/rentals/contracts/{d.Contract}/charges",
        $"/api/rentals/contracts/{d.Contract}/issue-notes",
        $"/api/rentals/contracts/{d.Contract}/return-notes",
        $"/api/rentals/contracts/{d.Contract}/extensions",
        $"/api/receivables/invoices/{d.Invoice}",
        $"/api/receivables/credit-notes/{d.CreditNote}",
        $"/api/receivables/receipts/{d.Receipt}",
        $"/api/ledger/entries/{d.Entry}",
        $"/api/payment-vouchers/{d.Voucher}",
        $"/api/sales/{d.Sale}",
    ];

    /// <summary>The same reads with ids nobody holds: the answer another branch's must be indistinguishable from.</summary>
    private static Docs Unknown() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task A_branch_manager_reads_another_branch_s_documents_by_id_exactly_as_ids_nobody_holds()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);

        foreach (var (foreign, unknown) in ReadsOf(s.Orn).Zip(ReadsOf(Unknown())))
        {
            var theirs = await s.Manager.GetAsync(foreign);
            var nobody = await s.Manager.GetAsync(unknown);
            Assert.True(theirs.StatusCode == HttpStatusCode.NotFound, $"{foreign} answered {(int)theirs.StatusCode}");
            await AssertSameAnswerAsync(nobody, theirs, foreign);
        }

        // The Admin still reads them, which is how the ids above are known to be real.
        foreach (var read in ReadsOf(s.Orn))
            Assert.True((await s.Admin.GetAsync(read)).StatusCode == HttpStatusCode.OK, read);
    }

    [Fact]
    public async Task A_branch_manager_still_reads_every_document_of_their_own_branch()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);

        foreach (var read in ReadsOf(s.Alger))
        {
            var response = await s.Manager.GetAsync(read);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{read} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        // Their own entry is whole: nothing folded.
        var entry = await s.Manager.GetFromJsonAsync<JsonElement>($"/api/ledger/entries/{s.Alger.Entry}");
        Assert.DoesNotContain(entry.GetProperty("lines").EnumerateArray(), l => l.GetProperty("folded").GetBoolean());
    }

    [Fact]
    public async Task A_company_scoped_role_that_is_not_the_admin_reads_every_branch_and_an_assigned_branch_role_reads_only_its_own()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        string[] reads =
        [
            PermissionCatalog.RentalsView, PermissionCatalog.InvoicesView, PermissionCatalog.ReceiptsView,
            PermissionCatalog.VouchersView, PermissionCatalog.AccountingView, PermissionCatalog.SalesView,
        ];
        using var controller = await CustomRoleUserAsync(s, "controle", s.Oran, RoleScope.Company, reads);
        using var keeper = await CustomRoleUserAsync(s, "comptable-alger", s.Main, RoleScope.AssignedBranch, reads);

        foreach (var read in ReadsOf(s.Orn).Concat(ReadsOf(s.Alger)))
            Assert.True((await controller.GetAsync(read)).StatusCode == HttpStatusCode.OK, $"company scope: {read}");
        foreach (var read in ReadsOf(s.Alger))
            Assert.True((await keeper.GetAsync(read)).StatusCode == HttpStatusCode.OK, $"assigned branch, own: {read}");
        foreach (var read in ReadsOf(s.Orn))
            Assert.True((await keeper.GetAsync(read)).StatusCode == HttpStatusCode.NotFound, $"assigned branch, other: {read}");
    }

    [Fact]
    public async Task Identity_copies_need_their_own_permission_and_the_contract_says_nothing_about_them_without_it()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);

        // customers.view and rentals.view are not enough, at their own branch: refused by the route,
        // whatever the ids, so the answer says nothing about any file.
        string[] files =
        [
            $"/api/catalog/customers/{s.Customer}/attachments",
            $"/api/catalog/customers/{s.Customer}/attachments/{s.CustomerScan}",
            $"/api/rentals/contracts/{s.Alger.Contract}/attachments",
            $"/api/rentals/contracts/{s.Alger.Contract}/attachments/{s.Alger.Attachment}",
        ];
        foreach (var file in files)
        {
            var refused = await s.Manager.GetAsync(file);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{file} answered {(int)refused.StatusCode}");
            Assert.Empty(await refused.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.Forbidden,
            (await s.Manager.GetAsync($"/api/catalog/customers/{Guid.NewGuid()}/attachments/{Guid.NewGuid()}")).StatusCode);

        // The contract they may read carries no copy, though one is kept.
        var contract = await s.Manager.GetFromJsonAsync<JsonElement>($"/api/rentals/contracts/{s.Alger.Contract}");
        Assert.Equal(0, contract.GetProperty("attachments").GetArrayLength());
        var asAdmin = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/rentals/contracts/{s.Alger.Contract}");
        Assert.Equal(1, asAdmin.GetProperty("attachments").GetArrayLength());

        // Adding a copy at the counter needs no more than before.
        var added = await s.Manager.PostAsync($"/api/catalog/customers/{s.Customer}/attachments", Upload(Jpeg, "verso.jpg"));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
    }

    [Fact]
    public async Task With_the_permission_a_branch_user_opens_their_branch_s_copies_and_no_guessed_or_swapped_id_reaches_another_s()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        using var clerk = await CustomRoleUserAsync(s, "dossiers-alger", s.Main, RoleScope.AssignedBranch,
            PermissionCatalog.RentalsView, PermissionCatalog.CustomersView, PermissionCatalog.CustomersDocumentsView);

        // Their own branch's contract: listed, read back byte for byte, and on the contract.
        var listed = await JsonAsync(await clerk.GetAsync($"/api/rentals/contracts/{s.Alger.Contract}/attachments"), HttpStatusCode.OK);
        Assert.Equal(s.Alger.Attachment, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
        var file = await clerk.GetAsync($"/api/rentals/contracts/{s.Alger.Contract}/attachments/{s.Alger.Attachment}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(Jpeg, await file.Content.ReadAsByteArrayAsync());
        var contract = await clerk.GetFromJsonAsync<JsonElement>($"/api/rentals/contracts/{s.Alger.Contract}");
        Assert.Equal(1, contract.GetProperty("attachments").GetArrayLength());
        // The customer's own copy: customers are the company's.
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync($"/api/catalog/customers/{s.Customer}/attachments/{s.CustomerScan}")).StatusCode);

        // Another branch's contract, and every mix of ids: not found, no bytes, no file headers.
        var oranContract = s.Orn.Contract.ToString();
        string[] guesses =
        [
            $"/api/rentals/contracts/{s.Orn.Contract}/attachments",
            $"/api/rentals/contracts/{s.Orn.Contract}/attachments/{s.Orn.Attachment}",
            $"/api/rentals/contracts/{s.Alger.Contract}/attachments/{s.Orn.Attachment}",
            $"/api/rentals/contracts/{s.Orn.Contract}/attachments/{s.Alger.Attachment}",
            $"/api/catalog/customers/{s.Customer}/attachments/{s.Orn.Attachment}",
            $"/api/rentals/contracts/{Guid.NewGuid()}/attachments/{s.Orn.Attachment}",
            $"/api/rentals/contracts/{{{oranContract}}}/attachments/{s.Orn.Attachment}",
            $"/api/rentals/contracts/{oranContract.ToUpperInvariant()}/attachments/{s.Orn.Attachment}",
            $"/api/rentals/contracts/{oranContract.Replace("-", "")}/attachments/{s.Orn.Attachment}",
        ];
        foreach (var guess in guesses)
        {
            var response = await clerk.GetAsync(guess);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{guess} answered {(int)response.StatusCode}");
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.Null(response.Content.Headers.ContentDisposition);
        }
        var oranRead = await clerk.GetFromJsonAsync<JsonElement>($"/api/rentals/contracts/{s.Alger.Contract}");
        Assert.DoesNotContain(oranRead.GetProperty("attachments").EnumerateArray(), a => a.GetProperty("id").GetGuid() == s.Orn.Attachment);

        // The Admin opens both branches' copies.
        Assert.Equal(Jpeg, await (await s.Admin.GetAsync($"/api/rentals/contracts/{s.Orn.Contract}/attachments/{s.Orn.Attachment}")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_unknown_contract_is_not_found_on_every_list_read_through_it_as_another_branch_s_is()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        var nobody = Guid.NewGuid();

        // Before P0.1b these answered 200 with an empty list, so an unknown contract and another
        // branch's (now 404) could be told apart.
        foreach (var list in new[] { "charges", "return-notes", "extensions", "attachments" })
            Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync($"/api/rentals/contracts/{nobody}/{list}")).StatusCode);
    }

    [Fact]
    public async Task A_branch_holding_part_of_an_inter_branch_entry_opens_it_from_its_statement_with_the_other_side_folded()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);

        // Booked at Oran: 300 out of Alger's cash, 300 to an Oran customer account.
        var shared = new LedgerEntry
        {
            EntryDate = DateTime.UtcNow, BranchId = s.Oran, SourceType = "test", SourceId = Guid.NewGuid(),
            Description = "Paiement pour Oran", IsInterBranch = true, CounterpartBranchId = s.Main, CreatedBy = "oran",
        };
        await ChangeAsync(host, async db =>
        {
            var cash = await db.LedgerAccounts.SingleAsync(a => a.Code == "530");
            var customers = await db.LedgerAccounts.SingleAsync(a => a.Code == "411");
            shared.Lines.Add(new LedgerEntryLine { AccountId = cash.Id, BranchId = s.Main, Debit = 300m, Memo = "caisse Alger" });
            shared.Lines.Add(new LedgerEntryLine { AccountId = customers.Id, BranchId = s.Oran, Credit = 300m, Memo = "client Oran" });
            db.LedgerEntries.Add(shared);
        });

        var entry = await JsonAsync(await s.Manager.GetAsync($"/api/ledger/entries/{shared.Id}"), HttpStatusCode.OK);
        var lines = entry.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        var own = Assert.Single(lines, l => !l.GetProperty("folded").GetBoolean());
        Assert.Equal(s.Main, own.GetProperty("branchId").GetGuid());
        Assert.Equal("530", own.GetProperty("accountCode").GetString());
        Assert.Equal(300m, own.GetProperty("debit").GetDecimal());
        var folded = Assert.Single(lines, l => l.GetProperty("folded").GetBoolean());
        Assert.Equal(300m, folded.GetProperty("credit").GetDecimal());
        Assert.Equal(Guid.Empty, folded.GetProperty("accountId").GetGuid());
        Assert.Equal(Guid.Empty, folded.GetProperty("branchId").GetGuid());
        Assert.Equal("", folded.GetProperty("accountCode").GetString());
        Assert.Equal(JsonValueKind.Null, folded.GetProperty("memo").ValueKind);
        // Nothing of Oran's side: not its account (by code or by id), not its memo.
        Assert.DoesNotContain(lines, l => l.GetProperty("accountCode").GetString() == "411");
        Assert.DoesNotContain((await AccountIdAsync(host, "411")).ToString(), entry.GetRawText());
        Assert.DoesNotContain("client Oran", entry.GetRawText());

        // The statement's drill reaches it: the Alger cash statement lists it.
        var statement = await s.Manager.GetFromJsonAsync<JsonElement>(
            $"/api/ledger/reports/account-statement?accountId={(await AccountIdAsync(host, "530"))}&take=1000");
        Assert.Contains(statement.GetProperty("rows").EnumerateArray(), r => r.GetProperty("entryId").GetGuid() == shared.Id);

        // Only the branch it is booked to reverses it; nothing is reversed.
        var reverse = await s.Manager.PostAsJsonAsync($"/api/ledger/entries/{shared.Id}/reverse", new { reason = "essai" });
        Assert.Equal(HttpStatusCode.NotFound, reverse.StatusCode);
        Assert.False(await ReadAsync(host, db => db.LedgerEntries.AnyAsync(e => e.ReversalOfEntryId == shared.Id)));

        // An Oran entry with no Alger line: as if it did not exist. The Admin reads the whole of both.
        await AssertSameAnswerAsync(await s.Manager.GetAsync($"/api/ledger/entries/{Guid.NewGuid()}"),
            await s.Manager.GetAsync($"/api/ledger/entries/{s.Orn.Entry}"), "oran entry");
        var whole = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/ledger/entries/{shared.Id}");
        Assert.Equal(2, whole.GetProperty("lines").EnumerateArray().Count(l => !l.GetProperty("folded").GetBoolean()));
    }

    [Fact]
    public async Task Another_branch_s_documents_named_in_a_body_or_a_query_are_answered_as_unknown_and_nothing_is_written()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        var before = await CountsAsync(host);

        // Refunds: the picker, and a note or a contract named on the voucher.
        var refundable = await s.Manager.GetFromJsonAsync<JsonElement>("/api/payment-vouchers/refundable");
        Assert.DoesNotContain(refundable.EnumerateArray(), n => n.GetProperty("returnNoteId").GetGuid() == s.Orn.ReturnNote);
        Assert.Equal(0, (await s.Manager.GetFromJsonAsync<JsonElement>(
            $"/api/payment-vouchers/refundable?returnNoteId={s.Orn.ReturnNote}")).GetArrayLength());

        await AssertSameAnswerAsync(
            await s.Manager.PostAsJsonAsync("/api/payment-vouchers", new
            {
                branchId = s.Main, kind = (int)PaymentVoucherKind.CustomerRefund, amount = 10m, methodKey = "cash", returnNoteId = Guid.NewGuid(),
            }),
            await s.Manager.PostAsJsonAsync("/api/payment-vouchers", new
            {
                branchId = s.Main, kind = (int)PaymentVoucherKind.CustomerRefund, amount = 10m, methodKey = "cash", returnNoteId = s.Orn.ReturnNote,
            }), "refund on a note");
        await AssertSameAnswerAsync(
            await s.Manager.PostAsJsonAsync("/api/payment-vouchers", new
            {
                branchId = s.Main, kind = (int)PaymentVoucherKind.CustomerRefund, amount = 10m, methodKey = "cash", contractId = Guid.NewGuid(),
            }),
            await s.Manager.PostAsJsonAsync("/api/payment-vouchers", new
            {
                branchId = s.Main, kind = (int)PaymentVoucherKind.CustomerRefund, amount = 10m, methodKey = "cash", contractId = s.Orn.Contract,
            }), "refund on a contract");
        await AssertSameAnswerAsync(
            await s.Manager.PostAsJsonAsync("/api/payment-vouchers", new
            {
                branchId = s.Main, kind = (int)PaymentVoucherKind.Expense, amount = 10m, methodKey = "cash",
                payeeName = "Transport", expenseCategory = "fuel", contractId = Guid.NewGuid(),
            }),
            await s.Manager.PostAsJsonAsync("/api/payment-vouchers", new
            {
                branchId = s.Main, kind = (int)PaymentVoucherKind.Expense, amount = 10m, methodKey = "cash",
                payeeName = "Transport", expenseCategory = "fuel", contractId = s.Orn.Contract,
            }), "expense on a contract");

        // Receipts: an invoice settled, a contract named.
        await AssertSameAnswerAsync(
            await s.Manager.PostAsJsonAsync("/api/receivables/receipts", new
            {
                customerId = s.Customer, branchId = s.Main, amount = 50m, methodKey = "card", reference = "TPE-2",
                allocations = new[] { new { invoiceId = Guid.NewGuid(), amount = 50m } },
            }),
            await s.Manager.PostAsJsonAsync("/api/receivables/receipts", new
            {
                customerId = s.Customer, branchId = s.Main, amount = 50m, methodKey = "card", reference = "TPE-2",
                allocations = new[] { new { invoiceId = s.Orn.Invoice, amount = 50m } },
            }), "receipt on an invoice");
        await AssertSameAnswerAsync(
            await s.Manager.PostAsJsonAsync("/api/receivables/receipts", new
            {
                customerId = s.Customer, branchId = s.Main, amount = 50m, methodKey = "card", reference = "TPE-3", contractId = Guid.NewGuid(),
            }),
            await s.Manager.PostAsJsonAsync("/api/receivables/receipts", new
            {
                customerId = s.Customer, branchId = s.Main, amount = 50m, methodKey = "card", reference = "TPE-3", contractId = s.Orn.Contract,
            }), "receipt on a contract");

        // Invoices raised over another branch's sale or note — "already invoiced" included.
        foreach (var (field, theirs) in new[] { ("saleId", s.Orn.Sale), ("issueNoteId", s.Orn.IssueNote), ("returnNoteId", s.Orn.ReturnNote) })
        {
            var nobody = new Dictionary<string, object> { ["dueDateUtc"] = DateTime.UtcNow.AddDays(30), [field] = Guid.NewGuid() };
            var oran = new Dictionary<string, object> { ["dueDateUtc"] = DateTime.UtcNow.AddDays(30), [field] = theirs };
            await AssertSameAnswerAsync(await s.Manager.PostAsJsonAsync("/api/receivables/invoices", nobody),
                await s.Manager.PostAsJsonAsync("/api/receivables/invoices", oran), $"invoice over {field}");
        }

        // The receipt form's "in exchange for" line says nothing of another branch's invoice.
        var purpose = await s.Manager.GetFromJsonAsync<JsonElement>($"/api/receivables/receipts/purpose?invoiceIds={s.Orn.Invoice}");
        Assert.Equal(JsonValueKind.Null, purpose.GetProperty("purpose").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, (await s.Manager.GetFromJsonAsync<JsonElement>(
            $"/api/receivables/receipts/purpose?invoiceIds={s.Alger.Invoice}")).GetProperty("purpose").ValueKind);

        // Sending another branch's receipt: the answer an unknown sale gets, and nothing is sent.
        await AssertSameAnswerAsync(
            await s.Manager.PostAsJsonAsync($"/api/sales/{Guid.NewGuid()}/send-receipt", new { channel = "email", to = "client@example.com" }),
            await s.Manager.PostAsJsonAsync($"/api/sales/{s.Orn.Sale}/send-receipt", new { channel = "email", to = "client@example.com" }),
            "send receipt");

        Assert.Equal(before, await CountsAsync(host));
    }

    [Fact]
    public async Task A_cashier_keeps_the_till_s_rule_and_the_admin_reads_every_branch()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        var (oranTill, _) = await SignedInWithBodyAsync(host, "caisse-oran", Password, null);
        using (oranTill)
        {
            Assert.Equal(HttpStatusCode.OK, (await oranTill.GetAsync($"/api/sales/{s.Orn.Sale}")).StatusCode);
            await AssertSameAnswerAsync(await oranTill.GetAsync($"/api/sales/{Guid.NewGuid()}"),
                await oranTill.GetAsync($"/api/sales/{s.Alger.Sale}"), "cashier, other branch's sale");
        }

        foreach (var read in ReadsOf(s.Orn).Concat(ReadsOf(s.Alger)))
            Assert.True((await s.Admin.GetAsync(read)).StatusCode == HttpStatusCode.OK, read);
    }

    // ---------- helpers ----------

    /// <summary>
    /// A user on a role the Admin made, of the given scope and permissions, signed in. Set in the
    /// database before the first sign-in, as the roles screen of AUTH-1 step 5 will.
    /// </summary>
    private static async Task<HttpClient> CustomRoleUserAsync(
        Scenario s, string name, Guid branch, RoleScope scope, params string[] permissions)
    {
        var (id, _) = await TestUsers.CreateAsync(s.Host, s.Admin, name, "Manager", branch, Password);
        var role = new Role { Name = name, Scope = scope };
        await ChangeAsync(s.Host, async db =>
        {
            db.Roles.Add(role);
            foreach (var key in permissions) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = key });
            (await db.Users.SingleAsync(u => u.Id == id)).RoleId = role.Id;
        });
        return await SignedInAsync(s.Host, name, Password, "C01");
    }

    /// <summary>The same status, headers and body: the caller cannot tell the two documents apart.</summary>
    private static async Task AssertSameAnswerAsync(HttpResponseMessage expected, HttpResponseMessage actual, string what)
    {
        Assert.True(expected.StatusCode == actual.StatusCode,
            $"{what}: {(int)actual.StatusCode} {await actual.Content.ReadAsStringAsync()} where an unknown id gets {(int)expected.StatusCode}");
        Assert.Equal(await expected.Content.ReadAsStringAsync(), await actual.Content.ReadAsStringAsync());
        Assert.Equal(expected.Content.Headers.ContentType?.MediaType, actual.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected.Content.Headers.ContentLength, actual.Content.Headers.ContentLength);
    }

    private static async Task<(int Vouchers, int Receipts, int Invoices, int CreditNotes, int Entries)> CountsAsync(PosHost host) =>
        await ReadAsync(host, async db => (
            await db.PaymentVouchers.CountAsync(), await db.ReceiptVouchers.CountAsync(), await db.Invoices.CountAsync(),
            await db.CreditNotes.CountAsync(), await db.LedgerEntries.CountAsync()));

    private static Task<Guid> AccountIdAsync(PosHost host, string code) =>
        ReadAsync(host, db => db.LedgerAccounts.Where(a => a.Code == code).Select(a => a.Id).SingleAsync());

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task ChangeAsync(PosHost host, Func<PosDbContext, Task> change)
    {
        await using var db = Db(host);
        await change(db);
        await db.SaveChangesAsync();
    }

    private static async Task<T> ReadAsync<T>(PosHost host, Func<PosDbContext, Task<T>> read)
    {
        await using var db = Db(host);
        return await read(db);
    }

    private static MultipartFormDataContent Upload(byte[] content, string name) =>
        new() { { new ByteArrayContent(content), "file", name } };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password, string? device) =>
        (await SignedInWithBodyAsync(host, username, password, device)).Client;

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInWithBodyAsync(
        PosHost host, string username, string password, string? device)
    {
        var client = host.CreateClient();
        var name = username == PosHost.BootstrapUsername ? username : host.SignInName(username);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = name, password, deviceCode = device });
        Assert.True(login.StatusCode == HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }
}
