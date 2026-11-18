using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// Someone the company buys from (owner, 2026-10-10: a basic suppliers module). Before it, goods
/// receiving posted one lump to account 401 with a free-text reference and a payment voucher named
/// its payee as free text, so no supplier had a balance or a statement.
///
/// <para>
/// <b>What moves the balance</b>: a receiving note naming the supplier (credit 401 — the company owes
/// more), a payment voucher paying the supplier (debit 401), the opening balance and its corrections.
/// <see cref="Balance"/> is what the company owes the supplier: positive means the company owes,
/// negative that the supplier holds money of the company's (an advance). The supplier statement is
/// account 401 for this supplier, read from <c>LedgerEntryLine.SupplierId</c>.
/// </para>
/// <para>
/// <b>Back-office only, not a <see cref="SyncEntity"/></b>: a till neither buys nor pays a supplier.
/// Soft-deleted only, like a customer: receipts and vouchers name it as history.
/// </para>
/// <para>Not in this step: purchase orders, supplier invoices, matching.</para>
/// </summary>
public class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Drawn by the server on create ("FRN-0001") and never changed.</summary>
    [MaxLength(32)] public string Code { get; set; } = string.Empty;

    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    [MaxLength(60)] public string? Phone { get; set; }
    [MaxLength(400)] public string? Address { get; set; }

    /// <summary>
    /// The supplier's fiscal identifiers keyed by the country profile's keys (NIF, RC, AI, NIS in the
    /// Algeria preset) — never a hardcoded label; the profile names them.
    /// </summary>
    public Dictionary<string, string> FiscalIds { get; set; } = new();

    [MaxLength(400)] public string? Note { get; set; }

    /// <summary>What the company owes this supplier; negative when the supplier owes the company.</summary>
    public decimal Balance { get; set; }

    /// <summary>
    /// What the company owed (positive) or was owed (negative) on the day it started on the system —
    /// written once, at registration or later by whoever holds <c>suppliers.opening_balance</c> when
    /// it was left empty, and corrected afterwards only by a <see cref="SupplierOpeningCorrection"/>
    /// with its reason (owner, 2026-10-10: the same rules as customers).
    /// </summary>
    public decimal OpeningBalance { get; set; }

    public DateOnly? OpeningBalanceOn { get; set; }
    public DateTime? OpeningBalanceSetAtUtc { get; set; }
    [MaxLength(200)] public string? OpeningBalanceSetBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A correction of a supplier's opening balance, with its reason — the supplier's mirror of
/// <see cref="CustomerOpeningCorrection"/>. Append-only; posts 401 for the supplier against
/// opening-balance equity (890).
/// </summary>
public class SupplierOpeningCorrection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(32)] public string Number { get; set; } = string.Empty;

    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public Guid BranchId { get; set; }

    /// <summary>Signed like <see cref="Supplier.Balance"/>: positive adds to what the company owes.</summary>
    public decimal Amount { get; set; }

    public DateOnly EffectiveOn { get; set; }
    [MaxLength(400)] public string Reason { get; set; } = string.Empty;
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
