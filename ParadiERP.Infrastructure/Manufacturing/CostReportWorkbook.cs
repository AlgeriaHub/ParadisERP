using System.Globalization;
using ParadiERP.Application.Contracts;
using ParadiERP.Infrastructure.Reports;

namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// The B2 cost reports as workbooks. Each is written from the very answer the screen receives — already scoped to
/// the caller's lines and already redacted for the caller's permissions — so an export can never show more than
/// the report. Nothing here reads the database. Financially finalized is written as words, never as a number.
/// </summary>
public static class CostReportWorkbook
{
    private static XlsxColumn Text(string header) => new(header);
    private static XlsxColumn Num(string header, int decimals = 3) => new(header, XlsxKind.Number, decimals);
    private static XlsxColumn When(string header) => new(header, XlsxKind.Date);

    public static string FileName(string report, DateOnly today) =>
        string.Create(CultureInfo.InvariantCulture, $"manufacturing-{report}-{today:yyyyMMdd}.xlsx");

    private static List<string> Title(CostReportLabels l, string titleKey, CostReportPeriodDto? period, bool redacted)
    {
        var lines = new List<string> { l[titleKey] };
        if (period is not null)
        {
            lines.Add($"{l["meta.period"]}: {Day(period.From)} – {Day(period.To)} · {l["meta.basis"]}: {l["basis." + period.Basis]}");
            lines.Add($"{l["meta.asOf"]}: {Day(period.AsOf)}");
        }
        lines.Add(l["meta.provisional"]);
        if (redacted) lines.Add(l["meta.redacted"]);
        return lines;
    }

    // dd/MM/yyyy in Latin digits, whatever the server's culture (CLAUDE.md: dates are the app's).
    private static string Day(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static object? Words(CostReportLabels l, string prefix, string? code) => code is null ? null : l[prefix + code];

    public static byte[] History(CostHistoryReportDto r, CostReportLabels l, Func<DateTime, DateTime> local)
    {
        var rows = r.Rows.Select(x => (IReadOnlyList<object?>)new object?[]
        {
            x.OrderNumber, x.LineCode, x.ProductSku, l["state." + x.State], Words(l, "kind.", x.Sheet?.Kind), Words(l, "source.", x.Sheet?.SourceState),
            x.Sheet is null ? null : local(x.Sheet.FrozenAtUtc), x.Sheet?.FrozenBy,
            x.Sheet?.CompletedAtUtc is DateTime c ? local(c) : null, x.Sheet?.FreezeLagDays,
            x.Figures is null ? null : x.AcceptedBaseQty, x.Figures?.Original, x.Figures?.LateRecorded, x.Figures?.LateRecognized,
            x.Figures?.LateUnclassified, x.Figures?.LateUncertain, x.Figures?.AdjustedProvisional,
            x.Figures is null ? null : l["value.notAvailable"], x.Figures?.OriginalUnitCost,
            Words(l, "result.", x.Integrity), string.Join(", ", x.GapCodes),
            x.Live?.Live, x.Live?.Unexplained,
        }).ToList();
        var t = r.Totals;
        return XlsxWriter.Write(new XlsxSheet
        {
            Name = l["title.history"], RightToLeft = l.RightToLeft, Title = Title(l, "title.history", r.Period, false),
            Columns =
            [
                Text(l["col.order"]), Text(l["col.line"]), Text(l["col.product"]), Text(l["col.state"]), Text(l["col.kind"]), Text(l["col.source"]),
                When(l["col.frozenAt"]), Text(l["col.frozenBy"]), When(l["col.completedAt"]), Num(l["col.lag"], 0), Num(l["col.accepted"]),
                Num(l["col.original"]), Num(l["col.lateRecorded"]), Num(l["col.lateRecognized"]), Num(l["col.lateUnclassified"]),
                Num(l["col.lateUncertain"]), Num(l["col.adjusted"]), Text(l["col.finalized"]), Num(l["col.unitCost"], 6),
                Text(l["col.integrity"]), Text(l["col.gaps"]), Num(l["col.live"]), Num(l["col.unexplained"]),
            ],
            Rows = rows,
            Totals = [l["value.total"], null, null, null, null, null, null, null, null, null, null,
                t.Original, t.LateRecorded, t.LateRecognized, t.LateUnclassified, t.LateUncertain, t.AdjustedProvisional,
                l["value.notAvailable"], null, null, null, null, null],
        });
    }

    public static byte[] LateCosts(LateCostReportDto r, CostReportLabels l, Func<DateTime, DateTime> local) => XlsxWriter.Write(new XlsxSheet
    {
        Name = l["title.late"], RightToLeft = l.RightToLeft, Title = Title(l, "title.late", r.Period, r.Rows.Any(x => x.Redacted)),
        Columns =
        [
            When(l["col.recordedAt"]), Text(l["col.order"]), Text(l["col.line"]), Text(l["col.kind"]), Text(l["col.element"]), Text(l["col.treatment"]),
            Num(l["col.recordedAmount"]), Num(l["col.recognized"]), Num(l["col.unclassified"]), Num(l["col.uncertain"]),
            Text(l["col.reference"]), Text(l["col.reason"]), Text(l["col.flags"]),
        ],
        Rows = r.Rows.Select(x => (IReadOnlyList<object?>)new object?[]
        {
            local(x.RecordedAtUtc), x.OrderNumber, x.LineCode, x.Kind, l["element." + (x.Element ?? "unclassified")], l["treatment." + x.Treatment],
            x.RecordedAmount, x.RecognizedEffect, x.UnclassifiedEffect, x.UncertainEffect,
            x.Redacted ? l["value.withheld"] : x.SourceReference, x.Redacted ? l["value.withheld"] : x.Reason, string.Join(", ", x.Flags),
        }).ToList(),
        Totals = [l["value.total"], null, null, null, null, null, r.Recorded, r.Recognized, r.Unclassified, r.Uncertain, null, null, null],
    });

    public static byte[] Variances(CostVarianceReportDto r, CostReportLabels l) => XlsxWriter.Write(new XlsxSheet
    {
        Name = l["title.variances"], RightToLeft = l.RightToLeft, Title = Title(l, "title.variances", r.Period, false),
        Columns = [Text(l["col.order"]), Text(l["col.line"]), Text(l["col.component"]), Num(l["col.original"]), Num(l["col.lateRecognized"]), Num(l["col.adjusted"]), Text(l["col.detail"])],
        Rows = r.Rows.SelectMany(x => x.Variance.Available
            ? x.Variance.Lines.Select(v => (IReadOnlyList<object?>)new object?[]
                { x.OrderNumber, x.LineCode, l["component." + v.Component], v.Original, v.LateRecognized, v.Adjusted, null })
            : [new object?[] { x.OrderNumber, x.LineCode, null, null, null, null, l["value.notAvailable"] + (x.Variance.Reason is null ? "" : $" ({x.Variance.Reason})") }])
            .ToList(),
    });

    public static byte[] Releases(ReleaseReconciliationReportDto r, CostReportLabels l, Func<DateTime, DateTime> local) => XlsxWriter.Write(new XlsxSheet
    {
        Name = l["title.releases"], RightToLeft = l.RightToLeft, Title = Title(l, "title.releases", r.Period, false),
        Columns =
        [
            Text(l["col.order"]), Text(l["col.line"]), Num(l["col.original"]), Text(l["col.release"]), When(l["col.releasedAt"]), Num(l["col.quantity"]),
            Num(l["col.value"]), Num(l["col.unitValue"], 6), Text(l["col.carriesRemainder"]), Num(l["col.catalogue"]),
            Num(l["col.unreleased"]), Num(l["col.keptInCharges"]), Num(l["col.lateNotAllocated"]), Text(l["col.flags"]),
        ],
        Rows = r.Rows.SelectMany(x =>
        {
            var rec = x.Reconciliation;
            var tail = new object?[] { rec.UnreleasedAcceptedQty, rec.KeptInCharges, rec.LateNotAllocated, string.Join(", ", rec.Flags) };
            return rec.Rows.Count == 0
                ? [(IReadOnlyList<object?>)new object?[] { x.OrderNumber, x.LineCode, x.Original, null, null, null, null, null, null, null }.Concat(tail).ToArray()]
                : rec.Rows.Select(v => (IReadOnlyList<object?>)new object?[]
                {
                    x.OrderNumber, x.LineCode, x.Original, v.Number, local(v.AtUtc), v.Quantity, v.Value, v.UnitValue,
                    v.CarriesRemainder ? l["value.yes"] : l["value.no"], v.CatalogueReferenceValue,
                }.Concat(tail).ToArray());
        }).ToList(),
    });

    public static byte[] Scrap(ScrapCostReportDto r, CostReportLabels l) => XlsxWriter.Write(new XlsxSheet
    {
        Name = l["title.scrap"], RightToLeft = l.RightToLeft, Title = Title(l, "title.scrap", r.Period, false),
        Columns =
        [
            Text(l["col.order"]), Text(l["col.line"]), Num(l["col.accepted"]), Num(l["col.rejected"]), Num(l["col.rejectionRate"], 2),
            Num(l["col.materialScrap"]), Num(l["col.recovery"]), Text(l["col.recoveryStatus"]), Num(l["col.spoilage"]), Num(l["col.wastage"]), Text(l["col.detail"]),
        ],
        Rows = r.Rows.Select(x => (IReadOnlyList<object?>)new object?[]
        {
            x.OrderNumber, x.LineCode, x.Scrap.AcceptedQty, x.Scrap.RejectedQty, x.Scrap.RejectionRatePercent, x.Scrap.MaterialScrapValue,
            x.Scrap.RecoveryValue, x.Scrap.RecoveryStatus, x.Scrap.SpoilageAbsorbed, x.Scrap.MaterialWastage, x.Scrap.Unavailable,
        }).ToList(),
        Totals = [l["value.total"], null, r.AcceptedQty, r.RejectedQty, null, r.MaterialScrapValue, null, null, r.SpoilageAbsorbed, null, null],
    });

    public static byte[] Gaps(CostGapReportDto r, CostReportLabels l) => XlsxWriter.Write(new XlsxSheet
    {
        Name = l["title.gaps"], RightToLeft = l.RightToLeft, Title = Title(l, "title.gaps", r.Period, false),
        Columns = [Text(l["col.order"]), Text(l["col.line"]), Text(l["col.state"]), Text(l["col.integrity"]), Text(l["col.severity"]),
            Text(l["col.code"]), Text(l["col.element"]), Num(l["col.value"]), Text(l["col.detail"])],
        Rows = r.Rows.SelectMany(x => x.Gaps.Select(g => (IReadOnlyList<object?>)new object?[]
        {
            x.OrderNumber, x.LineCode, l["state." + x.State], Words(l, "result.", x.Integrity), l["severity." + g.Severity], g.Code,
            g.Element is null ? null : l["element." + g.Element], g.Value, g.Detail is null ? null : (g.Code == "check_failed" ? l["check." + g.Detail] : g.Detail),
        })).ToList(),
    });

    public static byte[] Order(OrderCostReconciliationDto o, CostReportLabels l, Func<DateTime, DateTime> local)
    {
        var redacted = o.LateCosts.Any(x => x.Redacted) || o.Labour is { PerEmployeeMoneyShown: false };
        var head = Title(l, "title.order", null, redacted);
        head.Insert(1, $"{l["col.order"]}: {o.OrderNumber} · {l["col.line"]}: {o.LineCode} · {l["col.product"]}: {o.ProductSku} · {l["col.state"]}: {l["state." + o.State]}");
        head.Insert(2, $"{l["meta.asOf"]}: {Day(o.AsOf)}");
        var f = o.Figures;
        var figures = new XlsxSheet
        {
            Name = l["sheet.figures"], RightToLeft = l.RightToLeft, Title = head,
            Columns = [Text(l["col.component"]), Num(l["col.value"]), Text(l["col.detail"])],
            Rows = f is null ? [] :
            [
                [l["col.original"], f.Original, o.Sheet is null ? null : $"{l["kind." + o.Sheet.Kind]} · {l["source." + o.Sheet.SourceState]}"],
                [l["col.lateRecorded"], f.LateRecorded, null],
                [l["col.lateRecognized"], f.LateRecognized, null],
                [l["col.lateUnclassified"], f.LateUnclassified, null],
                [l["col.lateUncertain"], f.LateUncertain, null],
                [l["col.adjusted"], f.AdjustedProvisional, null],
                [l["col.finalized"], null, l["value.notAvailable"]],
                [l["col.unitCost"], f.OriginalUnitCost, null],
                .. o.Live is { } live ? new IReadOnlyList<object?>[]
                {
                    [l["col.live"], live.Live, live.Comparable ? null : live.Reason],
                    [l["col.unexplained"], live.Unexplained, null],
                } : [],
            ],
        };
        var elements = new XlsxSheet
        {
            Name = l["sheet.elements"], RightToLeft = l.RightToLeft, Title = [l["sheet.elements"]],
            Columns = [Text(l["col.element"]), Text(l["col.status"]), Num(l["col.original"]), Num(l["col.lateRecognized"]), Num(l["col.adjusted"])],
            Rows = [.. o.Elements.Select(e => (IReadOnlyList<object?>)new object?[] { l["element." + e.Element], e.Status, e.Original, e.LateRecognized, e.Adjusted }),
                .. o.RoundingResidual is decimal rho ? new IReadOnlyList<object?>[] { [l["element.rounding"], null, rho, null, rho] } : []],
        };
        var late = new XlsxSheet
        {
            Name = l["title.late"], RightToLeft = l.RightToLeft, Title = [l["title.late"]],
            Columns = [When(l["col.recordedAt"]), Text(l["col.kind"]), Text(l["col.element"]), Text(l["col.treatment"]), Num(l["col.recordedAmount"]),
                Text(l["col.reference"]), Text(l["col.reason"]), Text(l["col.flags"])],
            Rows = o.LateCosts.Select(x => (IReadOnlyList<object?>)new object?[]
            {
                local(x.RecordedAtUtc), x.Kind, l["element." + (x.Element ?? "unclassified")], l["treatment." + x.Treatment], x.RecordedAmount,
                x.Redacted ? l["value.withheld"] : x.SourceReference, x.Redacted ? l["value.withheld"] : x.Reason, string.Join(", ", x.Flags),
            }).ToList(),
        };
        var checks = new XlsxSheet
        {
            Name = l["sheet.checks"], RightToLeft = l.RightToLeft, Title = [l["sheet.checks"]],
            Columns = [Text(l["col.check"]), Text(l["col.result"]), Num(l["col.expected"], 6), Num(l["col.actual"], 6), Text(l["col.detail"])],
            Rows = o.Checks.Select(c => (IReadOnlyList<object?>)new object?[] { l["check." + c.Code], l["result." + c.Result], c.Expected, c.Actual, c.Detail }).ToList(),
        };
        return XlsxWriter.Write(figures, elements, late, checks);
    }
}
