namespace ParadiERP.Application.Contracts;

/// <summary>
/// A request that raises a document <b>at</b> a branch — a voucher, an invoice, a rental note, a
/// stock move, a payroll run. The branch it names is honoured for the Admin only; for everyone
/// else the API's <c>CallersBranch</c> filter replaces it with the caller's assigned branch before
/// the handler runs (<c>BranchLock</c>, the owner's "the branch choice does not appear except for
/// the super admin"). The field stays on every request so an older client still binds; for a
/// Manager or a Cashier its value is <b>ignored</b>.
/// </summary>
/// <remarks>
/// Implemented explicitly, so neither member is a public property and nothing new reaches the
/// JSON a client sends or receives.
/// </remarks>
public interface IRaisedAtBranch
{
    /// <summary>The branch the request names; null when it names none (an invoice over a source).</summary>
    Guid? RaisedAtBranch { get; }

    /// <summary>The same request at <paramref name="branchId"/>.</summary>
    IRaisedAtBranch AtBranch(Guid branchId);
}

/// <summary>
/// A request that takes money out of a named cash box. For a caller other than the Admin the box
/// must belong to their branch — a box id cannot be "replaced" the way a branch can, so a box of
/// another branch is refused in words instead.
/// </summary>
public interface IPaidFromBox
{
    Guid? PaidFromBox { get; }
}

// ---------- the documents ----------

/// <remarks><c>BranchId</c> is ignored for a non-admin: the voucher is the employee's branch — the
/// cash leaves the box of the branch that pays, even for a refund over a return note taken at
/// another branch.</remarks>
public partial record CreatePaymentVoucherRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin.</remarks>
public partial record CreateReceiptRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks>
/// <c>BranchId</c> is ignored for a non-admin. An invoice over a sale or a rental note still takes
/// that document's branch, as it always has: there the branch belongs to the paper it bills.
/// </remarks>
public partial record CreateInvoiceRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin, on create and on update.</remarks>
public partial record UpsertRentalContractRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin: the card's cash goes into the employee's branch box.</remarks>
public partial record IssueGiftCardRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin: a top-up or a cash refund moves the employee's branch box.</remarks>
public partial record AdjustGiftCardRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin: the goods leave from the employee's branch.</remarks>
public partial record IssueNoteRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks>
/// <c>BranchId</c> is ignored for a non-admin and replaced with the employee's branch; the return
/// is then refused unless that is the contract's branch — goods come back where the contract
/// belongs, and move on afterwards with a stock transfer.
/// </remarks>
public partial record ReturnNoteRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

// ---------- stock ----------

/// <remarks>
/// <c>BranchId</c> is ignored for a non-admin: a branch user receives into their own branch (owner,
/// 2026-10-12). The Admin names any branch, or none for the main warehouse.
/// </remarks>
public partial record ReceivingRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin.</remarks>
public partial record StockAdjustRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks>
/// <c>FromBranchId</c> is ignored for a non-admin: the goods leave the employee's branch. The
/// destination stays theirs to choose — it is a property of the move, and it must be another branch.
/// </remarks>
public partial record DispatchStockTransferRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => FromBranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { FromBranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin.</remarks>
public partial record OpeningBalanceRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin, exactly as for the opening balance it raises.</remarks>
public partial record OpeningImportRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin.</remarks>
public partial record StockStateChangeRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

// ---------- payroll and the money it moves ----------

/// <remarks><c>BranchId</c> is ignored for a non-admin.</remarks>
public partial record UpsertAttendanceSheetRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin.</remarks>
public partial record CreatePayrollRunRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks><c>BranchId</c> is ignored for a non-admin, and the box must be their branch's.</remarks>
public partial record RecordStatutoryPaymentRequest : IRaisedAtBranch, IPaidFromBox
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
    Guid? IPaidFromBox.PaidFromBox => CashBoxId;
}

/// <remarks>For a non-admin the box must be their branch's.</remarks>
public partial record GrantWageAdvanceRequest : IPaidFromBox
{
    Guid? IPaidFromBox.PaidFromBox => CashBoxId;
}

/// <remarks>For a non-admin the box must be their branch's.</remarks>
public partial record PayWagesRequest : IPaidFromBox
{
    Guid? IPaidFromBox.PaidFromBox => CashBoxId;
}

/// <remarks>
/// For a non-admin the <b>source</b> box must be their branch's; the destination — the safe the
/// takings go to — stays theirs to choose.
/// </remarks>
public partial record RemittanceRequest : IPaidFromBox
{
    Guid? IPaidFromBox.PaidFromBox => FromCashBoxId;
}

// ---------- records whose branch is a property of the record ----------

/// <remarks>
/// An employee's branch is a property of the employee, not a place a document is raised — but a
/// non-admin only ever files employees at their own branch, so for them it is replaced too.
/// </remarks>
public partial record UpsertEmployeeRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks>A device registered by a non-admin belongs to their branch.</remarks>
public partial record UpsertDeviceRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}

/// <remarks>
/// <c>BranchId</c> is ignored for a non-admin: a maintenance job is opened at the caller's branch, where its units are
/// in maintenance (Phase M1).
/// </remarks>
public partial record CreateMaintenanceJobRequest : IRaisedAtBranch
{
    Guid? IRaisedAtBranch.RaisedAtBranch => BranchId;
    IRaisedAtBranch IRaisedAtBranch.AtBranch(Guid branchId) => this with { BranchId = branchId };
}
