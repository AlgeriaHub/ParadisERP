using System.Security.Claims;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Security;
using static AlgeriaPos.Tests.Manufacturing.MfgHarness;

namespace AlgeriaPos.Tests.Manufacturing;

/// <summary>
/// A workshop for Phase 2's tests, written straight to the database: two lines on their own warehouses
/// (A at Atelier A, B at Atelier B), wire (raw material, kg, reference cost 150) and nails (finished, kg,
/// reference cost 400), an approved recipe for every line — 100 kg of nails from 100 kg of wire plus 5 %
/// waste, cut then formed (the output stage) — 300 kg of wire at A and 50 kg at B, and two employees on
/// both lines: Karim, whose attendance is recorded day by day (8 h on <see cref="Day"/>), and Samir,
/// whose attendance is declared per month.
/// </summary>
internal sealed record ProductionWorld(
    Guid LineA, Guid LineB, Guid Wire, Guid Nails, Guid Bom, Guid Karim, Guid Samir, Guid Admin, ClaimsPrincipal AdminPrincipal)
{
    public static readonly DateOnly Day = new(2026, 10, 20);
    public const string Orders = "/api/manufacturing/orders";
    public const string Order = "/api/manufacturing/orders/{id:guid}";

    public static async Task<ProductionWorld> SetupAsync(PosDbContext db)
    {
        var (admin, principal) = await AdminAsync(db);
        var wire = new Product { Sku = "WIRE-3", NameFr = "Fil 3 mm", NameAr = "سلك", NameEn = "Wire", Unit = "kg", Price = 0, CostPrice = 150m,
            ManufacturingRole = ManufacturingRole.RawMaterial, BaseUnitId = SystemUnits.Kilogram, TrackStock = true };
        var nails = new Product { Sku = "NAIL-70", NameFr = "Pointes 70", NameAr = "مسامير", NameEn = "Nails", Unit = "kg", Price = 500m, CostPrice = 400m,
            ManufacturingRole = ManufacturingRole.Finished, BaseUnitId = SystemUnits.Kilogram, TrackStock = true };
        db.Products.AddRange(wire, nails);
        var lineA = new ProductionLine { Code = "LA", NameAr = "خط أ", NameFr = "Ligne A", NameEn = "Line A", LocationId = WarehouseA, IsActive = true };
        var lineB = new ProductionLine { Code = "LB", NameAr = "خط ب", NameFr = "Ligne B", NameEn = "Line B", LocationId = WarehouseB, IsActive = true };
        db.ProductionLines.AddRange(lineA, lineB);
        // SQL Server enforces the branch/product parents while batching. Persist the independent
        // principals first so this reusable test world is provider-safe, not SQLite-only.
        await db.SaveChangesAsync();
        db.ProductionLineProducts.AddRange(
            new ProductionLineProduct { LineId = lineA.Id, ProductId = nails.Id },
            new ProductionLineProduct { LineId = lineB.Id, ProductId = nails.Id });
        var bom = new BillOfMaterials
        {
            ProductId = nails.Id, Version = 1, Status = BomStatus.Active, BatchQuantity = 100m, BatchUnitId = SystemUnits.Kilogram,
            CreatedBy = "test", ActivatedAtUtc = DateTime.UtcNow, ActivatedBy = "test",
            Lines = [new BomLine { Sequence = 1, ComponentProductId = wire.Id, Quantity = 100m, UnitId = SystemUnits.Kilogram, WastePercent = 5m, Specification = "Ø 3 mm" }],
            Stages =
            [
                new BomStage { Sequence = 1, NameAr = "القص", NameFr = "Coupe", NameEn = "Cut", EstimatedMinutes = 60, ExpectedWorkers = 2 },
                new BomStage { Sequence = 2, NameAr = "التشكيل", NameFr = "Frappe", NameEn = "Form", EstimatedMinutes = 30, ExpectedWorkers = 1, IsOutput = true },
            ],
        };
        db.BillsOfMaterials.Add(bom);
        db.ProductStocks.AddRange(
            new ProductStock { ProductId = wire.Id, BranchId = WarehouseA, StockQty = 300m },
            new ProductStock { ProductId = wire.Id, BranchId = WarehouseB, StockQty = 50m });

        var karim = new Employee { Number = "E-001", FullName = "Karim Benali", BranchId = TillHarness.Algiers, HiredOn = new DateOnly(2025, 1, 1),
            BaseRate = 52000m, AttendanceModeOverride = AttendanceMode.Recorded };
        var samir = new Employee { Number = "E-002", FullName = "Samir Haddad", BranchId = TillHarness.Algiers, HiredOn = new DateOnly(2025, 1, 1),
            BaseRate = 48000m, AttendanceModeOverride = AttendanceMode.Declared };
        db.Employees.AddRange(karim, samir);
        await db.SaveChangesAsync();
        foreach (var line in new[] { lineA, lineB })
        foreach (var person in new[] { karim, samir })
            db.ProductionLineEmployees.Add(new ProductionLineEmployee { LineId = line.Id, EmployeeId = person.Id, AssignedOn = new DateOnly(2026, 1, 1) });
        db.AttendanceDays.Add(new AttendanceDay { EmployeeId = karim.Id, WorkDate = Day, Status = AttendanceDayStatus.Present, HoursWorked = 8m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new ProductionWorld(lineA.Id, lineB.Id, wire.Id, nails.Id, bom.Id, karim.Id, samir.Id, admin.Id, principal);
    }

    /// <summary>An order body: <paramref name="kg"/> of nails on a line, each call its own request id unless one is given.</summary>
    public string OrderBody(Guid line, decimal kg, string? requestId = null, bool startShort = false, string? reason = null) =>
        JsonSerializer.Serialize(new
        {
            lineId = line, productId = Nails, plannedQty = kg, plannedUnitId = SystemUnits.Kilogram,
            plannedStart = "2026-10-19", plannedEnd = "2026-10-21", notes = (string?)null,
            clientRequestId = requestId ?? Guid.NewGuid().ToString("N"), startShort, shortageReason = reason,
        });

    public static (string, string) Of(Guid id) => ("id", id.ToString());

    public static string Movement(string kind, Guid material, decimal qty, string? requestId = null) =>
        JsonSerializer.Serialize(new
        {
            kind, lines = new[] { new { orderMaterialId = material, qty, unitId = SystemUnits.Kilogram } }, note = (string?)null,
            clientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        });

    public static string Release(decimal qty, string? requestId = null) =>
        JsonSerializer.Serialize(new { qty, unitId = SystemUnits.Kilogram, note = (string?)null, clientRequestId = requestId ?? Guid.NewGuid().ToString("N") });

    public static string Consume(Guid material, decimal qty, string reason = "Used", Guid? row = null, string? requestId = null) =>
        JsonSerializer.Serialize(new
        {
            orderMaterialId = material, baseQty = qty, reason, logRowId = row, note = (string?)null,
            clientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        });

    public async Task<JsonElement> CreateOrderAsync(PosDbContext db, Guid line, decimal kg) =>
        await OkAsync(db, AdminPrincipal, "POST", Orders, OrderBody(line, kg));

    public static Guid OrderId(JsonElement order) => order.GetProperty("order").GetProperty("id").GetGuid();
    public static Guid MaterialId(JsonElement order) => order.GetProperty("materials")[0].GetProperty("id").GetGuid();
    public static JsonElement Stage(JsonElement order, int index) => order.GetProperty("stages")[index];
}
