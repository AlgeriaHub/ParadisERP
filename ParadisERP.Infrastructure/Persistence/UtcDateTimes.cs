using ParadisERP.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ParadisERP.Infrastructure.Persistence;

/// <summary>
/// Every moment this system stores is UTC, and with this every one of them reads back saying so.
/// <para>
/// Neither provider keeps a <see cref="DateTime"/>'s kind: SQLite holds the value as text and SQL
/// Server as a <c>datetime2</c>, and both hand it back as <see cref="DateTimeKind.Unspecified"/>.
/// System.Text.Json writes an Unspecified value without a zone, and a browser reads a date and time
/// without a zone as its own clock. A transfer dispatched at 23:12 in Algiers read 22:12 on an
/// Algerian screen and 20:12 on one in Riyadh, and a moment shortly after midnight fell on the day
/// before. The prints, the receivables screens and the payroll routes had each been patching it on
/// the way out; marking the value as UTC where it is read is the one fix every screen needed.
/// </para>
/// <para>
/// Reading only. The converter's way in is the identity, and neither provider stores a kind anyway,
/// so a value is written exactly as before: no stored row changes and no migration follows. Nor
/// does anything on the server move — comparisons, sorting and arithmetic ignore the kind, and
/// every conversion to the company's zone already read these values as UTC.
/// </para>
/// </summary>
public static class UtcDateTimes
{
    /// <summary>
    /// The calendar days held in a <see cref="DateTime"/>: a date, not a moment — a birthday, the
    /// day a cheque may be cashed. They stay as they are; there is no instant to state a zone for.
    /// </summary>
    public static readonly IReadOnlySet<string> CalendarDays = new HashSet<string>(StringComparer.Ordinal)
    {
        $"{nameof(Customer)}.{nameof(Customer.BirthDate)}",
        $"{nameof(SalePayment)}.{nameof(SalePayment.InstrumentDueDate)}",
        $"{nameof(ReceiptVoucher)}.{nameof(ReceiptVoucher.InstrumentDueDate)}",
    };

    /// <summary>
    /// The moments whose name does not end in <c>Utc</c>. A ledger entry's date is the instant of the
    /// document behind it — the sale, the invoice, the payment — or the moment it was posted.
    /// </summary>
    public static readonly IReadOnlySet<string> InstantsNamedOtherwise = new HashSet<string>(StringComparer.Ordinal)
    {
        $"{nameof(LedgerEntry)}.{nameof(LedgerEntry.EntryDate)}",
    };

    /// <summary>In as it is; out marked UTC. EF never passes a null through, so it serves the nullable columns too.</summary>
    public static readonly ValueConverter<DateTime, DateTime> ReadAsUtc = new(
        value => value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary><c>Entity.Property</c>, the spelling <see cref="CalendarDays"/> and <see cref="InstantsNamedOtherwise"/> use.</summary>
    public static string NameOf(IReadOnlyProperty property) => $"{property.DeclaringType.ClrType.Name}.{property.Name}";

    public static bool IsDateTime(IReadOnlyProperty property) =>
        property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?);

    /// <summary>A moment in UTC: a <see cref="DateTime"/> named <c>…Utc</c>, or one of <see cref="InstantsNamedOtherwise"/>.</summary>
    public static bool IsInstant(IReadOnlyProperty property) =>
        IsDateTime(property)
        && !CalendarDays.Contains(NameOf(property))
        && (property.Name.EndsWith("Utc", StringComparison.Ordinal) || InstantsNamedOtherwise.Contains(NameOf(property)));

    /// <summary>
    /// Marks every instant in the model as read back in UTC. Called last in <c>OnModelCreating</c>,
    /// once every entity and property is in the model. A <see cref="DateTime"/> that is neither an
    /// instant nor a calendar day is left alone here and refused by <c>UtcDateTimeTests</c>, so a new
    /// one is decided rather than read back without a zone again.
    /// </summary>
    public static void Apply(ModelBuilder model)
    {
        foreach (var property in model.Model.GetEntityTypes().SelectMany(t => t.GetDeclaredProperties()).Where(IsInstant).ToList())
            property.SetValueConverter(ReadAsUtc);
    }
}
