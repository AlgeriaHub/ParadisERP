using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Paging;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17), for the sales and debtor-book
/// lists: gift cards and their history, suppliers with their statement and opening corrections, a
/// customer's opening corrections and quantity statement, the ageing board, invoices, receipts,
/// credit notes, the customer statement and payment vouchers. Each is asked a page at a time — ten
/// rows to begin with — its filters run on the server over every row, and its figures (tiles,
/// totals, a running balance) come over every filtered row, never the page alone.
/// </summary>
[Collection(HostCollection.Name)]
public class SalesPageTests
{
    private static async Task<HttpClient> SignedInAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Texts(JsonElement page, string field) =>
        page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty(field).GetString()!).ToArray();

    private static int Total(JsonElement page) => page.GetProperty("total").GetInt32();

    private static JsonElement Summary(JsonElement page) => page.GetProperty("summary");

    private static string Day(int offset) =>
        DateTime.UtcNow.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Seq(string prefix, int from, int to) => string.Join(",", Enumerable.Range(from, to - from + 1).Select(i => $"{prefix}{i:00}"));

    private static string Joined(string[] values) => string.Join(",", values);

    private static async Task<Guid> MainBranchAsync(PosDbContext db) =>
        await db.Branches.Where(b => b.IsMain && !b.IsWarehouse).Select(b => b.Id).FirstAsync();

    private static Customer Person(string code, string name) => new() { Code = code, FullName = name, Kind = CustomerKind.Person };

    // ---------- gift cards ----------

    [Fact]
    public async Task Gift_cards_page_with_the_states_the_table_says_and_a_card_history()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var now = DateTime.UtcNow;
        Guid withHistory;
        await using (var db = Db(host))
        {
            var customer = Person("GC-CUST", "Porteur");
            db.Customers.Add(customer);
            db.GiftCards.RemoveRange(db.GiftCards);
            for (var i = 1; i <= 23; i++)
            {
                db.GiftCards.Add(new GiftCard
                {
                    Code = $"GC-T-{i:00}", InitialAmount = 1000m,
                    Balance = i is 4 or 5 ? 0m : 1000m,
                    IsActive = i != 1,
                    ExpiresAtUtc = i switch { 2 => now.AddDays(-1), 3 => now.AddDays(1), _ => null },
                    CustomerId = i == 6 ? customer.Id : null,
                });
            }
            await db.SaveChangesAsync();
            withHistory = await db.GiftCards.Where(g => g.Code == "GC-T-06").Select(g => g.Id).SingleAsync();
            for (var i = 1; i <= 12; i++)
                db.GiftCardMovements.Add(new GiftCardMovement
                {
                    GiftCardId = withHistory, Delta = 10m, BalanceAfter = 1000m + 10m * i, Reason = "adjustment",
                    Note = $"M{i:00}", CreatedAtUtc = now.AddMinutes(-60 + i),
                });
            await db.SaveChangesAsync();
        }

        var first = await GetAsync(client, "/api/catalog/gift-cards/page");
        Assert.Equal(23, Total(first));
        Assert.Equal(10, first.GetProperty("take").GetInt32());
        Assert.Equal(Seq("GC-T-", 1, 10), Joined(Texts(first, "code")));

        Assert.Equal(["GC-T-01"], Texts(await GetAsync(client, "/api/catalog/gift-cards/page?state=inactive"), "code"));
        Assert.Equal(["GC-T-02"], Texts(await GetAsync(client, "/api/catalog/gift-cards/page?state=expired"), "code"));
        Assert.Equal(21, Total(await GetAsync(client, "/api/catalog/gift-cards/page?state=valid")));
        Assert.Equal(["GC-T-04", "GC-T-05"], Texts(await GetAsync(client, "/api/catalog/gift-cards/page?balance=spent"), "code"));
        Assert.Equal(["GC-T-06"], Texts(await GetAsync(client, "/api/catalog/gift-cards/page?customer=linked"), "code"));
        // The code search as a till types it, over every card, with the filters.
        Assert.Equal(["GC-T-20", "GC-T-21", "GC-T-22", "GC-T-23"], Texts(await GetAsync(client, "/api/catalog/gift-cards/page?q=gc-t-2"), "code"));
        Assert.Equal(["GC-T-21"], Texts(await GetAsync(client, "/api/catalog/gift-cards/page?q=gc-t-21&state=valid&balance=left"), "code"));

        // A page past the end is answered with the last page.
        var past = await GetAsync(client, "/api/catalog/gift-cards/page?skip=40&take=20");
        Assert.Equal(20, past.GetProperty("skip").GetInt32());
        Assert.Equal(["GC-T-21", "GC-T-22", "GC-T-23"], Texts(past, "code"));

        // A card's history: newest first, each row with its own balance after it.
        var history = await GetAsync(client, $"/api/catalog/gift-cards/{withHistory}/movements/page");
        Assert.Equal(12, Total(history));
        Assert.Equal("M12", Texts(history, "note")[0]);
        Assert.Equal(1120m, history.GetProperty("items")[0].GetProperty("balanceAfter").GetDecimal());
        Assert.Equal(["M02", "M01"], Texts(await GetAsync(client, $"/api/catalog/gift-cards/{withHistory}/movements/page?skip=10"), "note"));
    }

    // ---------- suppliers ----------

    [Fact]
    public async Task Suppliers_page_searches_and_filters_every_supplier()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await using (var db = Db(host))
        {
            db.Suppliers.RemoveRange(db.Suppliers);
            for (var i = 1; i <= 15; i++)
            {
                db.Suppliers.Add(new Supplier
                {
                    Code = $"SUP-T-{i:00}", Name = $"S{i:00}",
                    Phone = i == 3 ? null : $"05500000{i:00}",
                    Balance = i switch { 1 => 500m, 2 => -300m, _ => 0m },
                    FiscalIds = i == 4 ? new Dictionary<string, string> { ["nif"] = "000123" } : new Dictionary<string, string>(),
                });
            }
            await db.SaveChangesAsync();
        }

        var first = await GetAsync(client, "/api/suppliers/page");
        Assert.Equal(15, Total(first));
        Assert.Equal(Seq("S", 1, 10), Joined(Texts(first, "name")));

        Assert.Equal(["S01"], Texts(await GetAsync(client, "/api/suppliers/page?balance=weOwe"), "name"));
        Assert.Equal(["S02"], Texts(await GetAsync(client, "/api/suppliers/page?balance=owesUs"), "name"));
        Assert.Equal(13, Total(await GetAsync(client, "/api/suppliers/page?balance=settled")));
        Assert.Equal(["S03"], Texts(await GetAsync(client, "/api/suppliers/page?missing=phone"), "name"));
        Assert.Equal(14, Total(await GetAsync(client, "/api/suppliers/page?missing=fiscalIds")));
        // The search ignores case, over code, name and phone together.
        Assert.Equal(Seq("S", 10, 15), Joined(Texts(await GetAsync(client, "/api/suppliers/page?q=s1"), "name")));
        Assert.Equal(["S07"], Texts(await GetAsync(client, "/api/suppliers/page?q=0550000007"), "name"));

        var past = await GetAsync(client, "/api/suppliers/page?skip=30&take=10");
        Assert.Equal(10, past.GetProperty("skip").GetInt32());
        Assert.Equal(5, past.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Supplier_statement_page_carries_its_running_balance_and_the_corrections_page()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);

        // Opening 1000 owed to the supplier before the window; twelve corrections in it, odd ones
        // raising what is owed by 10·i, even ones lowering it by 10·i.
        var supplier = await PostAsync(client, "/api/suppliers", new
        {
            name = "Fournisseur relevé", openingBalance = new { amount = 1000m, direction = "weOwe", on = Day(-40) },
        });
        var id = supplier.GetProperty("id").GetGuid();
        for (var i = 1; i <= 12; i++)
            await PostAsync(client, $"/api/suppliers/{id}/opening-corrections", new
            {
                amount = 10m * i, direction = i % 2 == 1 ? "weOwe" : "owesUs", on = Day(-20 + i), reason = $"Correction {i}",
            });

        var window = $"from={Day(-30)}&to={Day(0)}";
        var first = await GetAsync(client, $"/api/suppliers/{id}/statement/page?{window}");
        Assert.Equal(12, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal(1000m, Summary(first).GetProperty("openingBalance").GetDecimal());
        Assert.Equal(1000m, Summary(first).GetProperty("pageOpening").GetDecimal());
        Assert.Equal(1010m, first.GetProperty("items")[0].GetProperty("balance").GetDecimal());

        var second = await GetAsync(client, $"/api/suppliers/{id}/statement/page?{window}&skip=10");
        var rows = second.GetProperty("items");
        Assert.Equal(2, rows.GetArrayLength());
        // The balance before the page: 1000 + 10·(1 − 2 + … − 10) = 950.
        Assert.Equal(950m, Summary(second).GetProperty("pageOpening").GetDecimal());
        Assert.Equal(1060m, rows[0].GetProperty("balance").GetDecimal());
        Assert.Equal(940m, rows[1].GetProperty("balance").GetDecimal());
        // The closing totals are the whole window's, whatever the page.
        Assert.Equal(940m, Summary(second).GetProperty("closingBalance").GetDecimal());
        Assert.Equal(420m, Summary(second).GetProperty("totalDebit").GetDecimal());
        Assert.Equal(360m, Summary(second).GetProperty("totalCredit").GetDecimal());

        // The opening corrections, oldest first.
        var corrections = await GetAsync(client, $"/api/suppliers/{id}/opening-corrections/page");
        Assert.Equal(12, Total(corrections));
        Assert.Equal("SOC-000001", Texts(corrections, "number")[0]);
        Assert.Equal(2, (await GetAsync(client, $"/api/suppliers/{id}/opening-corrections/page?skip=10")).GetProperty("items").GetArrayLength());

        // The statement route's own refusals.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/suppliers/{id}/statement/page?from={Day(0)}&to={Day(-5)}")).StatusCode);
    }

    // ---------- a customer's opening corrections ----------

    [Fact]
    public async Task Customer_opening_corrections_page_oldest_first()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        Guid id;
        await using (var db = Db(host))
        {
            var branch = await MainBranchAsync(db);
            var customer = Person("OBC-CUST", "Client corrigé");
            db.Customers.Add(customer);
            id = customer.Id;
            for (var i = 1; i <= 12; i++)
                db.CustomerOpeningCorrections.Add(new CustomerOpeningCorrection
                {
                    Number = $"OBC-T-{i:00}", CustomerId = id, BranchId = branch, Amount = 10m * i,
                    EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow), Reason = "Test",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-60 + i),
                });
            await db.SaveChangesAsync();
        }

        var first = await GetAsync(client, $"/api/catalog/customers/{id}/opening-corrections/page");
        Assert.Equal(12, Total(first));
        Assert.Equal(Seq("OBC-T-", 1, 10), Joined(Texts(first, "number")));
        Assert.Equal(["OBC-T-11", "OBC-T-12"], Texts(await GetAsync(client, $"/api/catalog/customers/{id}/opening-corrections/page?skip=10"), "number"));
    }

    // ---------- invoices ----------

    [Fact]
    public async Task Invoices_page_runs_the_chips_and_filters_over_every_invoice_and_sums_them()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var now = DateTime.UtcNow;
        await using (var db = Db(host))
        {
            var branch = await MainBranchAsync(db);
            var alpha = Person("INV-A", "Alpha BTP");
            var beta = Person("INV-B", "Beta Travaux");
            db.Customers.AddRange(alpha, beta);
            Invoice Make(int i, Customer c, DateTime issued) => new()
            {
                Number = $"INV-T-{i:00}", CustomerId = c.Id, BranchId = branch, IssuedAtUtc = issued,
                // The first three are ten days late; the others fall due in a month.
                DueDateUtc = i <= 3 ? now.AddDays(-10) : now.AddDays(30),
                TaxMode = i == 11 ? TaxMode.Untaxed : TaxMode.Taxed,
                Origin = i == 12 ? InvoiceOrigin.Sale : InvoiceOrigin.Direct,
                SubTotalHt = 1000m, GrandTotalTtc = 1000m,
            };
            var invoices = Enumerable.Range(1, 12).Select(i => Make(i, alpha, now.AddHours(-i))).ToList();
            var settled = Make(13, beta, now.AddDays(-60));
            var credited = Make(14, beta, now.AddDays(-60));
            db.Invoices.AddRange(invoices);
            db.Invoices.AddRange(settled, credited);
            // Paid in full and confirmed; a pending cheque speaks for 300 of the fourth.
            db.ReceiptVouchers.Add(new ReceiptVoucher
            {
                Number = "RCV-T-01", CustomerId = beta.Id, BranchId = branch, MethodKey = "cash", Status = PaymentStatus.Confirmed,
                Amount = 1000m, Allocations = [new ReceiptAllocation { InvoiceId = settled.Id, Amount = 1000m }],
            });
            db.ReceiptVouchers.Add(new ReceiptVoucher
            {
                Number = "RCV-T-02", CustomerId = alpha.Id, BranchId = branch, MethodKey = "cheque", Status = PaymentStatus.Pending,
                Amount = 300m, Allocations = [new ReceiptAllocation { InvoiceId = invoices[3].Id, Amount = 300m }],
            });
            db.CreditNotes.Add(new CreditNote
            {
                Number = "CRN-T-01", InvoiceId = credited.Id, CustomerId = beta.Id, BranchId = branch, Reason = "Annulée",
                SubTotalHt = 1000m, GrandTotalTtc = 1000m,
            });
            await db.SaveChangesAsync();
        }

        var all = await GetAsync(client, "/api/receivables/invoices/page");
        Assert.Equal(14, Total(all));
        // What is owed and late first, then what is owed, newest first; the settled and credited last.
        Assert.Equal("INV-T-01,INV-T-02,INV-T-03,INV-T-04,INV-T-05,INV-T-06,INV-T-07,INV-T-08,INV-T-09,INV-T-10",
            Joined(Texts(all, "number")));
        Assert.Equal(["Direct", "Sale"], Summary(all).GetProperty("origins").EnumerateArray().Select(o => o.GetString()!).Order().ToArray());

        var open = await GetAsync(client, "/api/receivables/invoices/page?state=open");
        Assert.Equal(12, Total(open));
        Assert.Equal(12, Summary(open).GetProperty("openCount").GetInt32());
        Assert.Equal(12000m, Summary(open).GetProperty("outstanding").GetDecimal());
        Assert.Equal(3000m, Summary(open).GetProperty("overdue").GetDecimal());
        Assert.Equal(300m, Summary(open).GetProperty("claimed").GetDecimal());

        Assert.Equal(3, Total(await GetAsync(client, "/api/receivables/invoices/page?state=overdue")));
        Assert.Equal(["INV-T-13"], Texts(await GetAsync(client, "/api/receivables/invoices/page?state=settled"), "number"));
        Assert.Equal(["INV-T-14"], Texts(await GetAsync(client, "/api/receivables/invoices/page?state=credited"), "number"));
        Assert.Equal(["INV-T-12"], Texts(await GetAsync(client, "/api/receivables/invoices/page?origin=Sale"), "number"));
        Assert.Equal(["INV-T-11"], Texts(await GetAsync(client, "/api/receivables/invoices/page?taxMode=Untaxed"), "number"));
        Assert.Equal(["INV-T-04"], Texts(await GetAsync(client, "/api/receivables/invoices/page?paper=yes"), "number"));
        // The finder folds case and accents, over the number, the customer and the notes.
        Assert.Equal(2, Total(await GetAsync(client, "/api/receivables/invoices/page?q=BETA")));
        // A window of the company's days: the two of sixty days ago fall out.
        var recent = await GetAsync(client, $"/api/receivables/invoices/page?from={Day(-30)}&to={Day(0)}");
        Assert.Equal(12, Total(recent));

        var past = await GetAsync(client, "/api/receivables/invoices/page?skip=100&take=10");
        Assert.Equal(10, past.GetProperty("skip").GetInt32());
        Assert.Equal(["INV-T-11", "INV-T-12", "INV-T-13", "INV-T-14"], Texts(past, "number").Order().ToArray());
    }

    // ---------- receipts ----------

    [Fact]
    public async Task Receipts_page_filters_the_paper_and_sums_the_tiles_over_the_state_asked_for()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var now = DateTime.UtcNow;
        await using (var db = Db(host))
        {
            var branch = await MainBranchAsync(db);
            var customer = Person("RCV-C", "Client chèques");
            db.Customers.Add(customer);
            var invoice = new Invoice
            {
                Number = "INV-R-01", CustomerId = customer.Id, BranchId = branch, DueDateUtc = now.AddDays(30),
                SubTotalHt = 1000m, GrandTotalTtc = 1000m,
            };
            db.Invoices.Add(invoice);
            for (var i = 1; i <= 11; i++)
                db.ReceiptVouchers.Add(new ReceiptVoucher
                {
                    Number = $"R-T-{i:00}", CustomerId = customer.Id, BranchId = branch, Status = PaymentStatus.Pending,
                    MethodKey = i == 2 ? "cheque" : "cash", Amount = 100m * i, ReceivedAtUtc = now.AddDays(-2),
                    InstrumentNumber = i == 3 ? "CHQ-777" : null,
                    InstrumentDueDate = i switch { 1 => now.AddDays(-1), 2 => now.AddDays(3), _ => now.AddDays(30).AddMinutes(i) },
                });
            db.ReceiptVouchers.Add(new ReceiptVoucher
            {
                Number = "R-T-12", CustomerId = customer.Id, BranchId = branch, Status = PaymentStatus.Confirmed,
                MethodKey = "cash", Amount = 500m, ReceivedAtUtc = now.AddDays(-1),
                Allocations = [new ReceiptAllocation { InvoiceId = invoice.Id, Amount = 200m }],
            });
            db.ReceiptVouchers.Add(new ReceiptVoucher
            {
                Number = "R-T-13", CustomerId = customer.Id, BranchId = branch, Status = PaymentStatus.Bounced,
                MethodKey = "cheque", Amount = 50m, ReceivedAtUtc = now.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var pending = await GetAsync(client, "/api/receivables/receipts/page?status=Pending");
        Assert.Equal(11, Total(pending));
        // Oldest due first, as the list route orders them.
        Assert.Equal(Seq("R-T-", 1, 10), Joined(Texts(pending, "number")));
        Assert.Equal(6600m, Summary(pending).GetProperty("pendingTotal").GetDecimal());
        Assert.Equal(100m, Summary(pending).GetProperty("dueTotal").GetDecimal());
        Assert.True(Summary(pending).GetProperty("hasPending").GetBoolean());
        Assert.Equal(["cash", "cheque"], Summary(pending).GetProperty("methodKeys").EnumerateArray().Select(k => k.GetString()!).ToArray());

        Assert.Equal(["R-T-01"], Texts(await GetAsync(client, "/api/receivables/receipts/page?status=Pending&due=overdue"), "number"));
        Assert.Equal(["R-T-02"], Texts(await GetAsync(client, "/api/receivables/receipts/page?status=Pending&due=soon"), "number"));
        Assert.Equal(["R-T-02"], Texts(await GetAsync(client, "/api/receivables/receipts/page?status=Pending&method=cheque"), "number"));
        Assert.Equal(["R-T-03"], Texts(await GetAsync(client, "/api/receivables/receipts/page?status=Pending&q=chq-777"), "number"));
        // The tiles stay those of the state asked for, whatever the finder.
        Assert.Equal(6600m, Summary(await GetAsync(client, "/api/receivables/receipts/page?status=Pending&q=chq-777")).GetProperty("pendingTotal").GetDecimal());

        var confirmed = await GetAsync(client, "/api/receivables/receipts/page?status=Confirmed&unallocated=yes");
        Assert.Equal(["R-T-12"], Texts(confirmed, "number"));
        Assert.Equal(300m, Summary(confirmed).GetProperty("unallocatedTotal").GetDecimal());
        Assert.Equal(13, Total(await GetAsync(client, "/api/receivables/receipts/page")));

        var past = await GetAsync(client, "/api/receivables/receipts/page?status=Pending&skip=50");
        Assert.Equal(10, past.GetProperty("skip").GetInt32());
        Assert.Equal(["R-T-11"], Texts(past, "number"));
    }

    // ---------- credit notes ----------

    [Fact]
    public async Task Credit_notes_page_newest_first_with_the_totals_of_every_note()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var now = DateTime.UtcNow;
        Guid customerId;
        await using (var db = Db(host))
        {
            var branch = await MainBranchAsync(db);
            var customer = Person("CRN-C", "Client avoirs");
            db.Customers.Add(customer);
            customerId = customer.Id;
            var invoice = new Invoice
            {
                Number = "INV-C-01", CustomerId = customer.Id, BranchId = branch, DueDateUtc = now.AddDays(30),
                SubTotalHt = 5000m, GrandTotalTtc = 5000m,
            };
            db.Invoices.Add(invoice);
            for (var i = 1; i <= 12; i++)
                db.CreditNotes.Add(new CreditNote
                {
                    Number = $"CN-T-{i:00}", InvoiceId = invoice.Id, CustomerId = customer.Id, BranchId = branch,
                    Reason = "Retour", IssuedAtUtc = now.AddHours(-13 + i),
                    SubTotalHt = 10m * i, TaxTotal = i, GrandTotalTtc = 11m * i,
                });
            await db.SaveChangesAsync();
        }

        var first = await GetAsync(client, $"/api/receivables/credit-notes/page?customerId={customerId}");
        Assert.Equal(12, Total(first));
        Assert.Equal("CN-T-12,CN-T-11,CN-T-10,CN-T-09,CN-T-08,CN-T-07,CN-T-06,CN-T-05,CN-T-04,CN-T-03", Joined(Texts(first, "number")));
        Assert.Equal("INV-C-01", first.GetProperty("items")[0].GetProperty("invoiceNumber").GetString());
        // 10·(1+…+12), (1+…+12) and 11·(1+…+12): every note, not the page.
        Assert.Equal(780m, Summary(first).GetProperty("ht").GetDecimal());
        Assert.Equal(78m, Summary(first).GetProperty("tax").GetDecimal());
        Assert.Equal(858m, Summary(first).GetProperty("ttc").GetDecimal());
        Assert.Equal(["CN-T-02", "CN-T-01"], Texts(await GetAsync(client, $"/api/receivables/credit-notes/page?customerId={customerId}&skip=10"), "number"));
    }

    // ---------- the customer statement ----------

    [Fact]
    public async Task Customer_statement_page_carries_its_running_balance_and_the_whole_window_totals()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        Guid id;
        await using (var db = Db(host))
        {
            var customer = Person("STM-C", "Client relevé");
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            id = customer.Id;
        }

        // Opening 1000 owed by the customer before the window; twelve corrections in it.
        await PostAsync(client, $"/api/catalog/customers/{id}/opening-balance", new { amount = 1000m, direction = "owesUs", on = Day(-40) });
        for (var i = 1; i <= 12; i++)
            await PostAsync(client, $"/api/catalog/customers/{id}/opening-corrections", new
            {
                amount = 10m * i, direction = i % 2 == 1 ? "owesUs" : "weOwe", on = Day(-20 + i), reason = $"Correction {i}",
            });

        var window = $"customerId={id}&from={Day(-30)}&to={Day(0)}";
        var whole = await GetAsync(client, $"/api/receivables/statement?{window}");
        var first = await GetAsync(client, $"/api/receivables/statement/page?{window}");
        var second = await GetAsync(client, $"/api/receivables/statement/page?{window}&skip=10");

        Assert.Equal(12, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal(1000m, Summary(first).GetProperty("pageOpening").GetDecimal());
        Assert.Equal(950m, Summary(second).GetProperty("pageOpening").GetDecimal());
        Assert.Equal(940m, second.GetProperty("items")[1].GetProperty("balance").GetDecimal());
        Assert.Equal(940m, Summary(second).GetProperty("closingBalance").GetDecimal());
        Assert.Equal(360m, Summary(second).GetProperty("totalDebit").GetDecimal());
        Assert.Equal(420m, Summary(second).GetProperty("totalCredit").GetDecimal());

        // The pages are the statement's rows, in its order, with its balances.
        var paged = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(r => r.GetProperty("balance").GetDecimal()).ToArray();
        Assert.Equal(whole.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("balance").GetDecimal()).ToArray(), paged);
        Assert.Equal(whole.GetProperty("closingBalance").GetDecimal(), Summary(first).GetProperty("closingBalance").GetDecimal());
    }

    // ---------- the ageing board ----------

    [Fact]
    public async Task Ageing_page_puts_the_latest_first_and_keeps_the_whole_book_figures()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var now = DateTime.UtcNow;
        await using (var db = Db(host))
        {
            var branch = await MainBranchAsync(db);
            for (var i = 1; i <= 12; i++)
            {
                var customer = Person($"AG-{i:00}", $"Ag-{i:00}");
                db.Customers.Add(customer);
                db.Invoices.Add(new Invoice
                {
                    Number = $"INV-AG-{i:00}", CustomerId = customer.Id, BranchId = branch, IssuedAtUtc = now.AddDays(-50),
                    // The first three forty days late; the others not yet due.
                    DueDateUtc = i <= 3 ? now.AddDays(-40) : now.AddDays(20),
                    SubTotalHt = 100m * i, GrandTotalTtc = 100m * i,
                });
            }
            await db.SaveChangesAsync();
        }

        var whole = await GetAsync(client, "/api/receivables/ageing");
        var first = await GetAsync(client, "/api/receivables/ageing/page");
        Assert.Equal(12, Total(first));
        // Latest first, then largest.
        Assert.Equal("Ag-03,Ag-02,Ag-01,Ag-12,Ag-11,Ag-10,Ag-09,Ag-08,Ag-07,Ag-06", Joined(Texts(first, "customerName")));
        Assert.Equal(["Ag-05", "Ag-04"], Texts(await GetAsync(client, "/api/receivables/ageing/page?skip=10"), "customerName"));
        Assert.Equal(7800m, Summary(first).GetProperty("total").GetDecimal());
        Assert.Equal(whole.GetProperty("total").GetDecimal(), Summary(first).GetProperty("total").GetDecimal());
        Assert.Equal(12, Summary(first).GetProperty("customerCount").GetInt32());
        Assert.Equal(12, Summary(first).GetProperty("listedCustomerIds").GetArrayLength());

        // The search narrows the board, never the book's figures.
        var found = await GetAsync(client, "/api/receivables/ageing/page?q=ag-1");
        Assert.Equal(["Ag-12", "Ag-11", "Ag-10"], Texts(found, "customerName"));
        Assert.Equal(7800m, Summary(found).GetProperty("total").GetDecimal());
    }

    // ---------- payment vouchers ----------

    [Fact]
    public async Task Payment_vouchers_page_filters_every_voucher_and_keeps_both_queues_totals()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var now = DateTime.UtcNow;
        await using (var db = Db(host))
        {
            var branch = await MainBranchAsync(db);
            for (var i = 1; i <= 11; i++)
                db.PaymentVouchers.Add(new PaymentVoucher
                {
                    Number = $"V-T-{i:00}", BranchId = branch, Status = PaymentVoucherStatus.Pending,
                    Kind = i == 5 ? PaymentVoucherKind.CustomerRefund : PaymentVoucherKind.Expense,
                    MethodKey = i == 2 ? "cheque" : "cash", Amount = 100m * i,
                    PayeeName = i == 3 ? "Transport Saïd" : "Fournisseur",
                    CreatedAtUtc = i == 11 ? now.AddDays(-40) : now.AddHours(-i),
                });
            db.PaymentVouchers.Add(new PaymentVoucher
            {
                Number = "V-T-12", BranchId = branch, Status = PaymentVoucherStatus.Approved, Kind = PaymentVoucherKind.Expense,
                MethodKey = "cash", Amount = 700m, CreatedAtUtc = now.AddHours(-20),
            });
            db.PaymentVouchers.Add(new PaymentVoucher
            {
                Number = "V-T-13", BranchId = branch, Status = PaymentVoucherStatus.Paid, Kind = PaymentVoucherKind.Expense,
                MethodKey = "cash", Amount = 50m, CreatedAtUtc = now.AddHours(-21),
            });
            await db.SaveChangesAsync();
        }

        var pending = await GetAsync(client, "/api/payment-vouchers/page?status=1");
        Assert.Equal(11, Total(pending));
        Assert.Equal(Seq("V-T-", 1, 10), Joined(Texts(pending, "number")));
        Assert.Equal(6600m, Summary(pending).GetProperty("pendingTotal").GetDecimal());
        Assert.Equal(700m, Summary(pending).GetProperty("approvedTotal").GetDecimal());
        Assert.Equal([1, 2], Summary(pending).GetProperty("kinds").EnumerateArray().Select(k => k.GetInt32()).ToArray());
        Assert.Equal(["cash", "cheque"], Summary(pending).GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("key").GetString()!).ToArray());

        Assert.Equal(["V-T-02"], Texts(await GetAsync(client, "/api/payment-vouchers/page?status=1&method=cheque"), "number"));
        Assert.Equal(["V-T-05"], Texts(await GetAsync(client, "/api/payment-vouchers/page?status=1&kind=1"), "number"));
        // The finder folds accents: "said" finds "Saïd".
        Assert.Equal(["V-T-03"], Texts(await GetAsync(client, "/api/payment-vouchers/page?status=1&q=said"), "number"));
        // A day range on the company's days.
        Assert.Equal(10, Total(await GetAsync(client, $"/api/payment-vouchers/page?status=1&from={Day(-10)}")));
        Assert.Equal(["V-T-11"], Texts(await GetAsync(client, $"/api/payment-vouchers/page?status=1&to={Day(-30)}"), "number"));

        var all = await GetAsync(client, "/api/payment-vouchers/page");
        Assert.Equal(13, Total(all));
        Assert.Equal(6600m, Summary(all).GetProperty("pendingTotal").GetDecimal());

        var past = await GetAsync(client, "/api/payment-vouchers/page?status=1&skip=40");
        Assert.Equal(10, past.GetProperty("skip").GetInt32());
        Assert.Equal(["V-T-11"], Texts(past, "number"));
    }

    // ---------- the customer quantity statement ----------

    [Fact]
    public async Task Quantity_statement_pages_its_items_and_contracts_with_the_statement_head()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branches = await GetAsync(client, "/api/catalog/branches");
        var branch = branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
        var customer = (await PostAsync(client, "/api/catalog/customers", new
        {
            code = "QTY-P1", fullName = "Client quantites", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = new DateTime(1980, 3, 4), creditLimit = 0m, kind = "Person",
            idDocumentNumber = "109 876 544",
        })).GetProperty("id").GetGuid();
        var site = (await PostAsync(client, $"/api/customers/{customer}/sites", new { name = "Chantier" })).GetProperty("id").GetGuid();
        var lines = new List<object>();
        for (var i = 1; i <= 12; i++)
        {
            var product = (await PostAsync(client, "/api/catalog/products", new
            {
                sku = $"QTY-P{i:00}", nameAr = $"سقالة {i}", nameFr = $"Échafaudage {i:00}", nameEn = $"Scaffold {i}",
                price = 900m, costPrice = 0m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
            })).GetProperty("id").GetGuid();
            await PostAsync(client, $"/api/catalog/products/{product}/stock",
                new { branchId = branch, qtyDelta = 100m, reason = "opening", reorderLevel = (decimal?)null });
            lines.Add(new { productId = product, qty = (decimal)i, durationDays = 30, rentUnitPrice = 50m, damageUnitPrice = 900m });
        }
        var contract = await PostAsync(client, "/api/rentals/contracts", new { customerId = customer, branchId = branch, siteId = site, lines });
        var contractId = contract.GetProperty("id").GetGuid();
        var issueLines = contract.GetProperty("lines").EnumerateArray()
            .Select(l => new { contractLineId = l.GetProperty("id").GetGuid(), qty = l.GetProperty("qty").GetDecimal() }).ToArray();
        await PostAsync(client, $"/api/rentals/contracts/{contractId}/issue-notes", new { branchId = branch, customerTransport = true, lines = issueLines });

        var query = $"customerId={customer}&current=true";
        var whole = await GetAsync(client, $"/api/rentals/quantity-statement?{query}");
        var first = await GetAsync(client, $"/api/rentals/quantity-statement/items/page?{query}");
        Assert.Equal(12, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        var second = await GetAsync(client, $"/api/rentals/quantity-statement/items/page?{query}&skip=10");
        // The pages are the statement's items, in its order.
        var paged = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("productId").GetGuid()).ToArray();
        Assert.Equal(whole.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("productId").GetGuid()).ToArray(), paged);
        // The head totals the whole statement, whatever the page.
        var head = Summary(second);
        Assert.Equal(12, head.GetProperty("itemCount").GetInt32());
        Assert.Equal(1, head.GetProperty("contractCount").GetInt32());
        Assert.Equal(whole.GetProperty("summary").GetProperty("withCustomer").GetDecimal(),
            head.GetProperty("summary").GetProperty("withCustomer").GetDecimal());
        Assert.Equal(78m, head.GetProperty("summary").GetProperty("withCustomer").GetDecimal());
        Assert.True(head.GetProperty("current").GetBoolean());

        var contracts = await GetAsync(client, $"/api/rentals/quantity-statement/contracts/page?{query}");
        Assert.Equal(1, Total(contracts));
        Assert.Equal(12, contracts.GetProperty("items")[0].GetProperty("lines").GetArrayLength());

        var past = await GetAsync(client, $"/api/rentals/quantity-statement/items/page?{query}&skip=40");
        Assert.Equal(10, past.GetProperty("skip").GetInt32());
        // The statement route's refusals, word for word the same query reader.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/rentals/quantity-statement/items/page?customerId={customer}&from=2026-03-01")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/rentals/quantity-statement/items/page?customerId={Guid.NewGuid()}&current=true")).StatusCode);
    }
}
