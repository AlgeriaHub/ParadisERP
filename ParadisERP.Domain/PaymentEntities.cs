using System.ComponentModel.DataAnnotations;

namespace ParadisERP.Domain;

/// <summary>
/// What a payment method actually *does*, as opposed to what it is called.
/// <para>
/// The behaviours below are programmed: <see cref="Credit"/> moves the customer's balance,
/// <see cref="GiftCard"/> a card balance, <see cref="Loyalty"/> points. <see cref="Generic"/>
/// is the catch-all for operator-defined tenders (postal transfer, bank transfer, cheque)
/// which only move money and carry no side effect of their own.
/// </para>
/// Frozen onto <see cref="SalePayment.Behavior"/> at push time so a later rename or
/// reconfiguration of the method cannot change how a historic sale is interpreted.
/// </summary>
public enum PaymentBehavior
{
    Cash = 0,
    Card = 1,
    Credit = 2,
    GiftCard = 3,
    Loyalty = 4,
    Generic = 5
}

/// <summary>
/// Whether the money behind a payment has actually arrived.
/// <para>
/// Cash is <see cref="Confirmed"/> the moment it is taken. Everything else defaults to
/// <see cref="Pending"/> — the customer counts as not having paid — until someone confirms it.
/// A cheque that fails becomes <see cref="Bounced"/>, which is a distinct state and not a
/// return to Pending: the money was expected, was counted as on its way, and did not come.
/// </para>
/// </summary>
public enum PaymentStatus
{
    Pending = 0,
    Confirmed = 1,
    Bounced = 2
}

/// <summary>
/// An operator-configurable tender. Replaces the closed <c>SupportedPaymentMethods</c>
/// whitelist, which could only ever enable a subset of a fixed enum and so could not express
/// a postal transfer.
/// <para>
/// Built-in definitions (<see cref="IsBuiltIn"/>) may be renamed, reordered, enabled and
/// disabled but not deleted, because code dispatches on their <see cref="Behavior"/>.
/// Operator-added definitions are always <see cref="PaymentBehavior.Generic"/>.
/// </para>
/// Replicated to tills like the rest of the catalog: the till needs the list at the moment
/// of payment, including offline.
/// </summary>
public class PaymentMethodDef : SyncEntity
{
    /// <summary>Stable lower-case identifier stored on every payment (<c>cash</c>, <c>cheque</c>).</summary>
    [MaxLength(40)] public string Key { get; set; } = string.Empty;

    [MaxLength(80)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(80)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(80)] public string NameEn { get; set; } = string.Empty;

    public PaymentBehavior Behavior { get; set; } = PaymentBehavior.Generic;

    /// <summary>
    /// Whether the money only counts once someone confirms it. Cash is false; a cheque or a
    /// postal transfer is true. Card is false by default because the terminal's approval
    /// number *is* the confirmation — the operator can still turn it on.
    /// </summary>
    public bool RequiresConfirmation { get; set; } = true;

    /// <summary>
    /// Whether this tender lands in the branch's cash box. Only physical cash does, so a
    /// cheque never inflates the drawer figure the Z-report reconciles against.
    /// </summary>
    public bool AffectsCashDrawer { get; set; }

    /// <summary>Whether the cashier must enter a reference (cheque number, transfer number).</summary>
    public bool RequiresReference { get; set; }

    /// <summary>Whether the cashier must enter the instrument's bank and due date.</summary>
    public bool RequiresInstrumentDetails { get; set; }

    /// <summary>Built-in behaviours cannot be deleted; code dispatches on them.</summary>
    public bool IsBuiltIn { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>The five tenders that carry programmed behaviour, seeded on every company.</summary>
    public static readonly string[] BuiltInKeys = ["cash", "card", "credit", "giftcard", "loyalty"];
}
