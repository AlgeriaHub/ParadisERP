namespace ParadisERP.Infrastructure.Receivables;

/// <summary>
/// The movement that raised this credit note automatically (owner, 2026-10-10): "sale" (a till refund,
/// SourceId = the refund sale's id) or "rental-return" (an early return, SourceId = the return note's id).
/// </summary>
public sealed record CreditNoteSource(string SourceType, Guid SourceId);
