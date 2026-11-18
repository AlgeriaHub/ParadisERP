using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;

namespace ParadisERP.Application.Rentals;

/// <summary>One contract line as the quantity statement names it.</summary>
public sealed record QuantityLine(Guid LineId, Guid ProductId, string Sku, string Name, string? NameAr, string? Barcode);

/// <summary>
/// One issue-note or return-note line at its real moment: <see cref="On"/> is the note's own
/// date-time in the company's time zone (<see cref="RentalPricing.LocalDay"/>) — never the period
/// start the pricing counts from. <see cref="IsCarryOver"/> marks the return + issue pair an
/// "extension as a new contract" writes for goods that never moved.
/// </summary>
public sealed record QuantityMove(
    Guid NoteId, string Number, Guid LineId, DateTime AtUtc, DateOnly On, Guid BranchId,
    bool IsIssue, bool IsCarryOver, decimal Issued, decimal Returned, decimal MissingDamaged);

/// <summary>One contract with everything the statement needs: the engine input it is priced from, and its moves.</summary>
public sealed record QuantityContract(
    Guid Id, string Number, Guid BranchId, Guid? SiteId, string? SiteName,
    DateOnly? GoodsBackOn, DateOnly? ClosedOn, bool PricingCorrectionPending,
    RentalContractInput Pricing, IReadOnlyList<QuantityLine> Lines, IReadOnlyList<QuantityMove> Moves);

/// <summary>Who the statement is for, and the customer's first issue note.</summary>
public sealed record QuantityStatementHeader(
    Guid CustomerId, string CustomerCode, string CustomerName, bool Current, DateOnly? FirstIssueOn);

/// <summary>
/// The customer quantity statement, worked out from the documents alone.
///
/// <para>
/// Quantities are counted by each note's real date in the company's zone: opening is everything
/// before From (no lower bound), the window is From..To with both ends included. The value is the
/// rental pricing of each stay cut to the window (<see cref="RentalStay.Clip"/>), with returns
/// matched to batches by <see cref="RentalFifo"/> exactly as <see cref="RentalSettlement"/> does.
/// Goods with no return yet are priced through To with the default rule and reported as
/// provisional.
/// </para>
/// </summary>
public static class QuantityStatement
{
    /// <summary>The contract's state at the end of <paramref name="to"/>.</summary>
    public static string StatusAsOf(DateOnly? goodsBackOn, DateOnly? closedOn, DateOnly to) =>
        closedOn is DateOnly closed && closed <= to ? "closed"
        : goodsBackOn is DateOnly back && back <= to ? "goodsBack"
        : "open";

    private static string RateKind(RentalStretch s) =>
        s.Kind == RentalChargeKind.Overdue ? "overdue" : s.ExtensionId is null ? "rent" : "extension";

    private static int RateOrder(string kind) => kind switch { "rent" => 0, "extension" => 1, _ => 2 };

    private static List<QuantityStatementRateDto> Ordered(IEnumerable<QuantityStatementRateDto> rates) =>
        rates.Distinct().OrderBy(r => RateOrder(r.Kind)).ThenBy(r => r.UnitPrice).ToList();

    /// <summary>Notes in the order they happened; an issue before a return at the same instant.</summary>
    private static IEnumerable<QuantityMove> Chronological(IEnumerable<QuantityMove> moves) =>
        moves.OrderBy(m => m.AtUtc).ThenBy(m => m.IsIssue ? 0 : 1).ThenBy(m => m.Number, StringComparer.Ordinal);

    private static decimal Delta(QuantityMove m) => m.IsIssue ? m.Issued : -(m.Returned + m.MissingDamaged);

    private sealed record Row(QuantityMove Move, string Kind, decimal Delta, decimal? UnitPrice);

    private sealed record LineFigures(
        QuantityContract Contract, QuantityLine Line, RentalLine Engine,
        decimal Opening, decimal Issued, decimal Returned, decimal MissingDamaged,
        decimal TransferredIn, decimal TransferredOut, DateOnly? FirstIssueOn, DateOnly? NegativeOn,
        decimal OverReturned, List<QuantityStatementRateDto> Rates, decimal Value, decimal Provisional,
        List<Row> Rows)
    {
        public decimal Remaining => Opening + Issued - Returned - MissingDamaged + TransferredIn - TransferredOut;
        public bool NoRent => Engine.RentUnitPrice == 0;
        public bool Shows => Opening != 0 || Remaining != 0 || Value != 0 || Rows.Any(r => r.Move.On >= From);
        public DateOnly From { get; init; }
    }

    public static QuantityStatementDto Build(
        QuantityStatementHeader header, IReadOnlyList<QuantityContract> contracts,
        DateOnly from, DateOnly to, RegionalSettings regional, Func<QuantityLine, bool>? lineFilter = null)
    {
        var lines = new List<LineFigures>();
        foreach (var contract in contracts)
            foreach (var line in contract.Lines)
            {
                if (lineFilter is not null && !lineFilter(line)) continue;
                var figures = Figures(contract, line, from, to, regional);
                if (figures is not null && figures.Shows) lines.Add(figures);
            }

        var items = lines
            .GroupBy(l => l.Line.ProductId)
            .Select(g => Item(g.ToList(), from))
            .OrderBy(i => i.Sku, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        var contractRows = lines
            .GroupBy(l => l.Contract.Id)
            .Select(g =>
            {
                var c = g.First().Contract;
                return new QuantityStatementContractDto(
                    c.Id, c.Number, StatusAsOf(c.GoodsBackOn, c.ClosedOn, to), c.BranchId,
                    c.SiteId, c.SiteName, g.Min(l => l.FirstIssueOn), c.PricingCorrectionPending,
                    g.Sum(l => l.Value), g.Sum(l => l.Provisional),
                    g.Select(l => new QuantityStatementContractLineDto(
                        l.Line.LineId, l.Line.ProductId, l.Line.Sku, l.Line.Name, l.Line.NameAr, l.FirstIssueOn,
                        l.Opening, l.Issued, l.Returned, l.MissingDamaged, l.TransferredIn, l.TransferredOut, l.Remaining,
                        l.Engine.RentUnitPrice, l.NoRent, l.Rates, l.Value, l.Provisional)).ToList());
            })
            .OrderBy(c => c.FirstIssueOn).ThenBy(c => c.Number, StringComparer.Ordinal)
            .ToList();

        var warnings = new List<QuantityStatementWarningDto>();
        foreach (var l in lines)
        {
            if (l.NegativeOn is DateOnly on)
                warnings.Add(new("negativeBalance", l.Line.ProductId, l.Line.Sku, l.Line.Name, l.Line.NameAr,
                    l.Contract.Id, l.Contract.Number, on, null));
            if (l.OverReturned > 0)
                warnings.Add(new("overReturned", l.Line.ProductId, l.Line.Sku, l.Line.Name, l.Line.NameAr,
                    l.Contract.Id, l.Contract.Number, null, l.OverReturned));
        }
        foreach (var c in contractRows.Where(c => c.PricingCorrectionPending))
            warnings.Add(new("pricingCorrectionPending", null, null, null, null, c.ContractId, c.Number, null, null));

        var summary = new QuantityStatementSummaryDto(
            items.Count, items.Sum(i => i.Issued), items.Sum(i => i.Returned), items.Sum(i => i.MissingDamaged),
            items.Sum(i => i.Remaining), items.Sum(i => i.Value), items.Sum(i => i.ProvisionalValue));

        return new QuantityStatementDto(
            header.CustomerId, header.CustomerCode, header.CustomerName, from, to, header.Current, header.FirstIssueOn,
            summary, items, contractRows, warnings);
    }

    /// <summary>One contract line: its quantities as of To, its rows, and its value in the window.</summary>
    private static LineFigures? Figures(
        QuantityContract contract, QuantityLine line, DateOnly from, DateOnly to, RegionalSettings regional)
    {
        var engine = contract.Pricing.Lines.FirstOrDefault(l => l.Id == line.LineId);
        if (engine is null) return null;
        var unitPrice = engine.RentUnitPrice == 0 ? (decimal?)null : engine.RentUnitPrice;

        // ---- quantities, by each note's real day ----
        decimal opening = 0, issued = 0, returned = 0, missing = 0, transferIn = 0, transferOut = 0, held = 0;
        DateOnly? firstIssue = null, negativeOn = null;
        var rows = new List<Row>();
        foreach (var m in Chronological(contract.Moves.Where(m => m.LineId == line.LineId && m.On <= to)))
        {
            var delta = Delta(m);
            held += delta;
            if (held < 0 && negativeOn is null) negativeOn = m.On;
            if (m.IsIssue && !m.IsCarryOver && (firstIssue is null || m.On < firstIssue)) firstIssue = m.On;

            var kind = m.IsCarryOver
                ? (m.IsIssue ? "transferIn" : "transferOut")
                : m.IsIssue ? "issue" : held > 0 ? "partialReturn" : "return";
            rows.Add(new Row(m, kind, delta, unitPrice));

            if (m.On < from) { opening += delta; continue; }
            if (m.IsCarryOver)
            {
                if (m.IsIssue) transferIn += m.Issued; else transferOut += m.Returned + m.MissingDamaged;
            }
            else if (m.IsIssue) issued += m.Issued;
            else { returned += m.Returned; missing += m.MissingDamaged; }
        }

        // ---- value, priced exactly as the settlement prices it, cut to the window ----
        var duration = RentalSettlement.DurationOf(engine, contract.Pricing.Lines);
        var extensions = RentalSettlement.ExtensionsFor(engine, contract.Pricing.Extensions);
        var entitlement = RentalSettlement.EntitlementFor(engine, duration, extensions);
        var batches = RentalFifo.Oldest(contract.Pricing.Issues.Where(i => i.LineId == engine.Id));
        var notes = contract.Pricing.Returns.Where(r => r.LineId == engine.Id).OrderBy(r => r.ReturnedOn).ToList();

        var left = new Dictionary<IssueBatch, decimal>(ReferenceEqualityComparer.Instance);
        foreach (var b in batches) left[b] = left.GetValueOrDefault(b) + b.Qty;

        var rates = new List<QuantityStatementRateDto>();
        if (engine.RentUnitPrice > 0) rates.Add(new("rent", engine.RentUnitPrice));
        decimal value = 0, provisional = 0, overReturned = 0;

        void Add(IReadOnlyList<RentalStretch> stretches, decimal qty, bool isProvisional)
        {
            var inside = RentalStay.Clip(stretches, from, to);
            var amount = RentalStay.Value(regional, inside, qty);
            value += amount;
            if (isProvisional) provisional += amount;
            rates.AddRange(inside.Where(s => s.UnitPrice > 0 && s.Days > 0).Select(s => new QuantityStatementRateDto(RateKind(s), s.UnitPrice)));
        }

        var allocations = RentalSettlement.AllocateLine(engine, duration, batches, notes, extensions,
            regional, contract.Pricing.MinimumDays);
        overReturned = Math.Max(0m, notes.Sum(n => n.SettledQty) - allocations.Sum(a => a.Qty));
        foreach (var allocation in allocations)
        {
            left[allocation.Batch] -= allocation.Qty;
        }
        foreach (var group in allocations.SelectMany(a => a.Stay.Stretches.Select(s => (Allocation: a, Stretch: s)))
            .GroupBy(x => (x.Allocation.ReturnOrdinal, x.Allocation.Batch.IssueNoteId, x.Stretch)))
            Add([group.Key.Stretch], group.Sum(x => x.Allocation.Qty), isProvisional: false);

        // Still out with no return note at all: priced through To with the default rule — overdue
        // charged, no waiver, no minimum (the minimum is charged when they come back).
        foreach (var (batch, qty) in left)
        {
            if (qty <= 0 || batch.IssuedOn > to) continue;
            var stay = RentalStay.Price(batch.IssuedOn, to.AddDays(1), entitlement, duration, engine.OverdueUnitPrice);
            Add(stay.Stretches, qty, isProvisional: true);
        }

        return new LineFigures(
            contract, line, engine, opening, issued, returned, missing, transferIn, transferOut,
            firstIssue, negativeOn, overReturned, Ordered(rates), value, provisional, rows) { From = from };
    }

    /// <summary>One item across its lines, with one running balance over every row.</summary>
    private static QuantityStatementItemDto Item(List<LineFigures> lines, DateOnly from)
    {
        var first = lines[0].Line;
        var opening = lines.Sum(l => l.Opening);
        var movements = new List<QuantityStatementMovementDto>
        {
            new(from, null, "opening", null, null, null, null, opening, 0, 0, null, opening, null, null)
        };
        var balance = opening;
        var rows = lines
            .SelectMany(l => l.Rows.Where(r => r.Move.On >= from).Select(r => (Line: l, Row: r)))
            .OrderBy(x => x.Row.Move.AtUtc).ThenBy(x => x.Row.Move.IsIssue ? 0 : 1)
            .ThenBy(x => x.Row.Move.Number, StringComparer.Ordinal);
        foreach (var (l, r) in rows)
        {
            balance += r.Delta;
            var m = r.Move;
            movements.Add(new QuantityStatementMovementDto(
                m.On, m.AtUtc, r.Kind, m.NoteId, m.Number, l.Contract.Id, l.Contract.Number,
                r.Delta, m.Returned, m.MissingDamaged, r.UnitPrice, balance,
                m.BranchId, l.Contract.SiteName));
        }

        var issued = lines.Sum(l => l.Issued);
        var returned = lines.Sum(l => l.Returned);
        var missing = lines.Sum(l => l.MissingDamaged);
        var transferNet = lines.Sum(l => l.TransferredIn - l.TransferredOut);
        return new QuantityStatementItemDto(
            first.ProductId, first.Sku, first.Name, first.NameAr, first.Barcode,
            opening, issued, returned, missing, transferNet, opening + issued - returned - missing + transferNet,
            Ordered(lines.SelectMany(l => l.Rates)), lines.All(l => l.NoRent),
            lines.Sum(l => l.Value), lines.Sum(l => l.Provisional), lines.Any(l => l.NegativeOn is not null),
            movements);
    }
}
