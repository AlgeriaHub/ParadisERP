using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// A prepaid card. Replicated to tills so they can check it offline; the balance is owned by
/// the server and overwritten on the next pull, like stock.
/// </summary>
public class GiftCard : SyncEntity
{
    [MaxLength(40)] public string Code { get; set; } = string.Empty;
    public decimal InitialAmount { get; set; }
    public decimal Balance { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAtUtc { get; set; }
    public Guid? CustomerId { get; set; }
}

/// <summary>Audit trail of every gift card balance change.</summary>
public class GiftCardMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GiftCardId { get; set; }
    public decimal Delta { get; set; }
    public decimal BalanceAfter { get; set; }
    /// <summary>issue, redeem, refund, adjustment.</summary>
    [MaxLength(32)] public string Reason { get; set; } = string.Empty;
    [MaxLength(200)] public string? Note { get; set; }
    public Guid? SaleId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Audit trail of every loyalty point change.</summary>
public class LoyaltyMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public int Delta { get; set; }
    public int BalanceAfter { get; set; }
    /// <summary>earn, redeem, refund, refund-redeem.</summary>
    [MaxLength(32)] public string Reason { get; set; } = string.Empty;
    public Guid? SaleId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
