using Microsoft.EntityFrameworkCore;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;

namespace AlgeriaPos.Infrastructure.Seeding;

public static class DbSeeder
{
    public static async Task SeedAsync(PosDbContext db, RegionalSettings regional, string? adminPassword, CancellationToken ct = default)
    {
        if (!await db.SyncCounters.AnyAsync(ct))
            db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        await db.SaveChangesAsync(ct);

        // Built-in tenders and cash boxes are not demo data: an existing company upgrading
        // into this release has neither, and the early return below would skip them forever.
        await CoreDataSeeder.EnsureAsync(db, ct);

        if (await db.Users.AnyAsync(ct)) return; // already seeded

        if (adminPassword is not { Length: >= 8 })
            throw new InvalidOperationException("Set Bootstrap__AdminPassword (at least 8 characters) to initialize a demo administrator.");

        // One transaction: version reservation and the rows it stamps must commit together.
        await using var tx = await db.BeginWriteAsync(ct);

        var admin = new User
        {
            Username = "admin",
            FullName = "Administrateur",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            PinHash = BCrypt.Net.BCrypt.HashPassword("1234"),
            Role = UserRole.Admin
        };
        var cashier = new User
        {
            Username = "caisse1",
            FullName = "Caissier 1",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Caisse@123"),
            PinHash = BCrypt.Net.BCrypt.HashPassword("1111"),
            Role = UserRole.Cashier
        };
        await db.TouchAsync(admin, ct);
        await db.TouchAsync(cashier, ct);
        db.Users.AddRange(admin, cashier);

        var store = new Store
        {
            NameAr = "مؤسسة الخرسانة والمقاولات",
            NameFr = "Béton & Travaux BTP",
            NameEn = "Concrete & Construction Works",
            Address = "Zone Industrielle, Rouiba, Alger",
            AddressFr = "Zone Industrielle, Rouiba, Alger",
            Phone = "+213 21 00 00 00",
            // Keys match the Algeria country preset, the default profile.
            FiscalIds = new()
            {
                ["nif"] = "000016001234567",
                ["nis"] = "000016009876543",
                ["rc"] = "16/00-1234567B21",
                ["ai"] = "16010112345"
            },
            ReceiptFooter = "Merci de votre confiance / شكرا لثقتكم",
            ReceiptFooterFr = "Merci de votre confiance / شكرا لثقتكم"
        };
        await db.TouchAsync(store, ct);
        db.Stores.Add(store);

        // The main warehouse receives stock from suppliers; "Alger-Centre" is the main branch — the head
        // office, which sells and holds the demo tills and the central safe. A second selling branch
        // is seeded too, empty on purpose, so a fresh install can demonstrate a transfer immediately.
        // Their location codes come from the save (BranchCodeAssignment): Alger-Centre 10, the
        // depot 11, Oran 12 — main branch, main warehouse, then the rest.
        var mainBranch = new Branch
        {
            NameAr = "المستودع المركزي", NameFr = "Dépôt central", NameEn = "Central depot",
            IsWarehouse = true, IsMain = true, AllowNegativeStock = true, IsActive = true
        };
        var centreBranch = new Branch
        {
            NameAr = "فرع الجزائر الوسط", NameFr = "Alger-Centre", NameEn = "Algiers-Centre",
            Address = "12 Rue Didouche Mourad, Alger", Phone = "+213 21 00 00 00", IsMain = true, IsActive = true
        };
        var oranBranch = new Branch
        {
            NameAr = "فرع وهران", NameFr = "Oran", NameEn = "Oran",
            Address = "Boulevard de la Soummam, Oran", IsActive = true
        };
        await db.TouchAsync(mainBranch, ct);
        await db.TouchAsync(centreBranch, ct);
        await db.TouchAsync(oranBranch, ct);
        db.Branches.AddRange(mainBranch, centreBranch, oranBranch);

        var categories = new[]
        {
            new Category { NameAr = "خرسانة جاهزة", NameFr = "Béton prêt à l'emploi", NameEn = "Ready-mix concrete", Color = "#64748b", SortOrder = 1 },
            new Category { NameAr = "مواد البناء", NameFr = "Matériaux de construction", NameEn = "Building materials", Color = "#f59e0b", SortOrder = 2 },
            new Category { NameAr = "حديد التسليح", NameFr = "Acier d'armature", NameEn = "Reinforcing steel", Color = "#0ea5e9", SortOrder = 3 },
            new Category { NameAr = "خدمات ومقاولات", NameFr = "Services & travaux", NameEn = "Services & works", Color = "#16a34a", SortOrder = 4 }
        };
        foreach (var c in categories) await db.TouchAsync(c, ct);
        db.Categories.AddRange(categories);

        // A ready-mix concrete and building-contracting supplier: bulk materials sold by volume,
        // weight or the bag, plus the site services invoiced alongside them. Construction supplies
        // are not staples, so everything here sits at the Algeria preset's 19% standard rate.
        var products = new[]
        {
            NewProduct("B25", "6130000000017", "خرسانة جاهزة ب25", "Béton prêt B25", "Ready-mix concrete B25", categories[0].Id, 12000m, 19m, "m³"),
            NewProduct("B30", "6130000000024", "خرسانة جاهزة ب30", "Béton prêt B30", "Ready-mix concrete B30", categories[0].Id, 13500m, 19m, "m³"),
            NewProduct("CIM50", "6130000000031", "إسمنت 50 كغ", "Ciment 50 kg", "Cement 50 kg", categories[1].Id, 850m, 19m, "sac"),
            NewProduct("SAB", "6130000000048", "رمل مغسول", "Sable lavé", "Washed sand", categories[1].Id, 2800m, 19m, "m³"),
            NewProduct("GRV", "6130000000055", "حصى 15/25", "Gravier 15/25", "Gravel 15/25", categories[1].Id, 3200m, 19m, "m³"),
            NewProduct("BRQ12", "6130000000062", "آجر 12 فتحة", "Brique 12 trous", "Hollow brick 12", categories[1].Id, 45m, 19m, "u"),
            NewProduct("PAR20", "6130000000079", "بلوك خرساني 20", "Parpaing 20", "Concrete block 20", categories[1].Id, 95m, 19m, "u"),
            NewProduct("FER12", "6130000000086", "حديد تسليح 12 مم", "Acier à béton 12 mm", "Rebar 12 mm", categories[2].Id, 135000m, 19m, "t"),
            // Site services are invoiced on the same ticket but hold no stock.
            NewProduct("POMP", "6130000000093", "ضخ الخرسانة", "Pompage béton", "Concrete pumping", categories[3].Id, 18000m, 19m, "service", trackStock: false),
            NewProduct("TRSP", "6130000000109", "نقل بالشاحنة", "Transport camion", "Truck haulage", categories[3].Id, 6000m, 19m, "service", trackStock: false)
        };
        foreach (var p in products)
        {
            if (!regional.TaxRates.Contains(p.TaxRate)) p.TaxRate = regional.DefaultTaxRate;
            await db.TouchAsync(p, ct);
        }
        db.Products.AddRange(products);

        // Alger-Centre gets exactly the demo quantities the till sells against; the central depot
        // gets a buffer on top, so a fresh install can transfer stock to Oran (which starts empty)
        // without needing a purchase/receiving entry first. Services hold no stock at all, so they
        // get no rows — a quantity against "concrete pumping" would be meaningless.
        // The seeded quantities are the demo company's opening position, so the books have to
        // be told. Without this, inventory is worth nothing in the ledger while the warehouse
        // is full, and account 310 goes negative the moment anything sells — the demo would
        // show the exact §5.3 error the ledger exists to prevent, on a fresh install.
        var openingAtCentre = new List<ValuedLine>();
        var openingAtMain = new List<ValuedLine>();

        foreach (var (product, stock) in products.Zip(seedStock))
        {
            if (!product.TrackStock) continue;
            // A flat reorder level cannot serve both 25 tonnes of rebar and 12 000 bricks.
            var reorder = Math.Ceiling(stock * 0.15m);
            db.ProductStocks.Add(new ProductStock { ProductId = product.Id, BranchId = centreBranch.Id, StockQty = stock, ReorderLevel = reorder });
            db.ProductStocks.Add(new ProductStock { ProductId = product.Id, BranchId = mainBranch.Id, StockQty = stock * 2, ReorderLevel = 0 });

            // Valued at cost, the same figure a sale later relieves inventory at, so the two
            // sides agree and the stock account trends to zero as the goods are sold.
            openingAtCentre.Add(new ValuedLine(product.Sku, stock * product.CostPrice));
            openingAtMain.Add(new ValuedLine(product.Sku, stock * 2 * product.CostPrice));
        }

        // Authored rows may say what they know. This does not contradict "the migration never
        // guesses a kind": these are written by hand, not inferred from a row that already
        // existed. The walk-in is an anonymous natural person at the counter — a permanent
        // "kind not set" chip on the one row that will never have details would be noise.
        var walkIn = new Customer { Code = "C000", FullName = "Client de passage / زبون عابر", Kind = CustomerKind.Person };
        // Contracting runs on account: site deliveries are invoiced against a credit limit,
        // which is why these are far larger than a retail customer's. Both are companies, each
        // with the person who signs for it, so a fresh install demonstrates the feature and a
        // contract raised on C001 gets its signatory prefilled.
        var credit = new Customer { Code = "C001", FullName = "Entreprise Ben Ali BTP / مقاولات بن علي", Phone = "+213 555 11 22 33", CreditLimit = 2_000_000m, Kind = CustomerKind.Company };
        var promoter = new Customer { Code = "C002", FullName = "SARL El Injaz Travaux / شركة الإنجاز للأشغال", Phone = "+213 555 44 55 66", CreditLimit = 5_000_000m, Kind = CustomerKind.Company };
        await db.TouchAsync(walkIn, ct);
        await db.TouchAsync(credit, ct);
        await db.TouchAsync(promoter, ct);
        db.Customers.AddRange(walkIn, credit, promoter);

        var seededAt = DateTime.UtcNow;
        db.CustomerRepresentatives.AddRange(
            new CustomerRepresentative
            {
                CustomerId = credit.Id, FullName = "Karim Ben Ali", IdDocumentNumber = "109 876 543",
                Phone = "+213 555 11 22 33", Note = "gérant", FromUtc = seededAt, CreatedBy = "seed"
            },
            new CustomerRepresentative
            {
                CustomerId = promoter.Id, FullName = "Nadia Bouzid", IdDocumentNumber = "118 765 432",
                Phone = "+213 555 44 55 66", Note = "gérante", FromUtc = seededAt, CreatedBy = "seed"
            });

        db.Devices.Add(new Device { Code = "C01", Name = "Caisse 1", BranchId = centreBranch.Id });
        db.Devices.Add(new Device { Code = "C02", Name = "Caisse 2", BranchId = centreBranch.Id });

        await db.SaveChangesAsync(ct);

        // Posted after the save so the accounts exist and the entries carry real branch ids.
        // Against opening-balance equity, never a purchase: these goods were not bought today.
        var ledger = new LedgerPoster(db);
        await ledger.PostAsync(
            StockPosting.Opening(Guid.NewGuid(), centreBranch.Id, centreBranch.NameFr, openingAtCentre), ct);
        await ledger.PostAsync(
            StockPosting.Opening(Guid.NewGuid(), mainBranch.Id, mainBranch.NameFr, openingAtMain), ct);
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        // The branches only exist now, so their cash boxes could not be created above.
        await CoreDataSeeder.EnsureAsync(db, ct);
    }

    // Matches the product order above: m³ of concrete, bags, m³ of aggregate, units, tonnes,
    // then the two services, which hold no stock.
    private static readonly decimal[] seedStock = { 150, 120, 800, 200, 180, 12000, 8000, 25, 0, 0 };

    private static Product NewProduct(
        string sku, string barcode, string ar, string fr, string en,
        Guid categoryId, decimal price, decimal taxRate, string unit, bool trackStock = true) => new()
    {
        Sku = sku,
        Barcode = barcode,
        NameAr = ar,
        NameFr = fr,
        NameEn = en,
        CategoryId = categoryId,
        Price = price,
        CostPrice = Math.Round(price * 0.75m, 2),
        TaxRate = taxRate,
        Unit = unit,
        TrackStock = trackStock
    };
}
