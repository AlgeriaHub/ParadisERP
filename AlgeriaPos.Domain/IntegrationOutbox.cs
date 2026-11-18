using System.ComponentModel.DataAnnotations;

namespace AlgeriaPos.Domain;

/// <summary>
/// An event waiting to be delivered to the company's webhook. Written in the same transaction as
/// the sale it describes, so an accepted sale is never lost to a failed delivery.
/// </summary>
public class IntegrationOutbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SaleId { get; set; }
    [MaxLength(40)] public string EventType { get; set; } = "sale.accepted";
    public string Payload { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    [MaxLength(500)] public string? LastError { get; set; }
}
