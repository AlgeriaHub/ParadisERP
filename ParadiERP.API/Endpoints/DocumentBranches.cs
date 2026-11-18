using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The branch a document named by id belongs to, for <see cref="CallersBranch.ForBranchOf{TBuilder}"/>
/// (security review, 2026-10-10). <c>null</c> when there is no such document.
/// </summary>
public static class DocumentBranches
{
    public static Task<Guid?> Contract(PosDbContext db, Guid id, CancellationToken ct) =>
        db.RentalContracts.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> Voucher(PosDbContext db, Guid id, CancellationToken ct) =>
        db.PaymentVouchers.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> Invoice(PosDbContext db, Guid id, CancellationToken ct) =>
        db.Invoices.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> CreditNote(PosDbContext db, Guid id, CancellationToken ct) =>
        db.CreditNotes.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> Receipt(PosDbContext db, Guid id, CancellationToken ct) =>
        db.ReceiptVouchers.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    /// <summary>A pending till tender belongs to the branch of the sale it was taken on.</summary>
    public static Task<Guid?> SalePayment(PosDbContext db, Guid id, CancellationToken ct) =>
        db.SalePayments.AsNoTracking().Where(x => x.Id == id)
            .Join(db.Sales, p => p.SaleId, s => s.Id, (p, s) => (Guid?)s.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> LedgerEntry(PosDbContext db, Guid id, CancellationToken ct) =>
        db.LedgerEntries.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> Device(PosDbContext db, Guid id, CancellationToken ct) =>
        db.Devices.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> CashBox(PosDbContext db, Guid id, CancellationToken ct) =>
        db.CashBoxes.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> PayrollRun(PosDbContext db, Guid id, CancellationToken ct) =>
        db.PayrollRuns.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> WagePayment(PosDbContext db, Guid id, CancellationToken ct) =>
        db.WagePayments.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    /// <summary>An advance belongs to the branch that held the employee when it was granted.</summary>
    public static Task<Guid?> WageAdvance(PosDbContext db, Guid id, CancellationToken ct) =>
        db.WageAdvances.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    /// <summary>A maintenance job belongs to the branch where its units are (Phase M1).</summary>
    public static Task<Guid?> MaintenanceJob(PosDbContext db, Guid id, CancellationToken ct) =>
        db.MaintenanceJobs.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    /// <summary>A row of MS-04's workshop list belongs to the branch whose list it is.</summary>
    public static Task<Guid?> MaintenanceWorkshopMember(PosDbContext db, Guid id, CancellationToken ct) =>
        db.MaintenanceWorkshopMembers.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> Employee(PosDbContext db, Guid id, CancellationToken ct) =>
        db.Employees.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BranchId).FirstOrDefaultAsync(ct);
}
