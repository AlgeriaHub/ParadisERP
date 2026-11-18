using System.Globalization;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Hr;

/// <summary>
/// The fingerprint terminal's monthly export, shown as it would be read — and nothing written
/// (FR-3B).
///
/// <para>
/// A person is found by the number on their card (<c>Employee.FingerprintNumber</c>) and by
/// nothing else. The name column, when the file has one, is shown beside the employee's own name
/// for the reader to compare; it never picks anybody, because two people share a name far more
/// often than a terminal mislabels a number. A number nobody carries is reported with the names
/// the file gives it, so the clerk knows whose card to fill in.
/// </para>
/// <para>
/// A number that belongs to an employee of <b>another</b> branch is said as such and not
/// named: the caller is previewing one branch's month, and a manager of that branch has no
/// business reading who works elsewhere.
/// </para>
/// </summary>
public sealed class PunchImportPreview(PosDbContext db)
{
    /// <summary>Rows sent back that are not matched, in sheet order. Every row is still counted.</summary>
    public const int ProblemRowsShown = 1000;

    /// <summary>Matched rows sent back. The per-employee table says the rest.</summary>
    public const int MatchedRowsShown = 300;

    public const int TopRows = 12;
    public const int MaxColumns = 40;

    /// <param name="headerChoice">Null to take the suggestion, 0 for "no header row", else that row.</param>
    public async Task<HrResult<PunchImportPreviewDto>> PreviewAsync(
        Guid branchId, string fileName, Worksheet sheet, int? headerChoice, PunchMapping mapping,
        int earlyHour = PunchSheet.DefaultEarlyHour,
        CancellationToken ct = default)
    {
        if (!await db.Branches.AnyAsync(b => b.Id == branchId, ct))
            return HrResult<PunchImportPreviewDto>.Invalid(HrProblems.BranchUnknown, "That branch does not exist.");

        var suggested = PunchSheet.GuessHeaderRow(sheet);
        int? header = headerChoice switch
        {
            null => suggested,
            <= 0 => null,
            var row => row
        };

        var suggestedMapping = PunchSheet.GuessMapping(sheet, header);
        var suggestedDto = suggestedMapping is null ? null : new PunchMappingDto(
            suggestedMapping.Number, suggestedMapping.Name, suggestedMapping.Date, suggestedMapping.Time,
            suggestedMapping.DateTime, suggestedMapping.Marker, suggestedMapping.TimeOut);

        var columns = Columns(sheet, header);
        var top = sheet.Rows.Take(TopRows)
            .Select(r => new PunchTopRowDto(r.Number, r.Cells
                .Where(c => c.Key <= MaxColumns)
                .ToDictionary(c => c.Key, c => c.Value.Text)))
            .ToList();
        var order = mapping.Order.ToString();

        var problem = PunchSheet.MappingProblem(mapping);
        if (problem is not null)
            return HrResult<PunchImportPreviewDto>.Success(new PunchImportPreviewDto(
                fileName, sheet.Name, sheet.SheetNames, header, suggested, order, columns, top,
                problem, null, [], false, [], [], [], suggestedDto));

        var punches = PunchSheet.Read(sheet, header, mapping, earlyHour);

        var numbers = punches.Where(p => p.Problem is null).Select(p => p.Number!).Distinct().ToList();
        var holders = numbers.Count == 0
            ? []
            : await db.Employees.AsNoTracking()
                .Where(e => e.FingerprintNumber != null && numbers.Contains(e.FingerprintNumber))
                .Select(e => new { e.Id, e.Number, e.FullName, e.BranchId, Fingerprint = e.FingerprintNumber! })
                .ToListAsync(ct);
        var byNumber = holders.ToDictionary(h => h.Fingerprint, StringComparer.Ordinal);

        var rows = punches.Select(p =>
        {
            if (p.Problem is not null)
                return new PunchRowDto(p.Row, "invalid", p.Problem, p.Number, p.Name, null, p.Marker, p.Source, Exit: p.Exit, Flags: p.Flags);
            var at = Stamp(p.At!.Value);
            if (!byNumber.TryGetValue(p.Number!, out var holder))
                return new PunchRowDto(p.Row, "unknown", null, p.Number, p.Name, at, p.Marker, p.Source, Exit: p.Exit, Flags: p.Flags);
            if (holder.BranchId != branchId)
                return new PunchRowDto(p.Row, "otherBranch", null, p.Number, p.Name, at, p.Marker, p.Source, Exit: p.Exit, Flags: p.Flags);
            return new PunchRowDto(p.Row, "matched", null, p.Number, p.Name, at, p.Marker, p.Source,
                holder.Id, holder.Number, holder.FullName, p.Exit, p.Flags);
        }).ToList();

        var readable = punches.Where(p => p.At is not null).Select(p => p.At!.Value).ToList();
        var summary = new PunchSummaryDto(
            rows.Count,
            rows.Count(r => r.Status == "matched"),
            rows.Count(r => r.Status == "unknown"),
            rows.Count(r => r.Status == "otherBranch"),
            rows.Count(r => r.Status == "invalid"),
            punches.Count(p => p.TextDate),
            readable.Count == 0 ? null : Stamp(readable.Min()),
            readable.Count == 0 ? null : Stamp(readable.Max()),
            punches.Count(p => p.Flags is { Count: > 0 }));

        var employees = rows.Where(r => r.Status == "matched")
            .GroupBy(r => r.EmployeeId!.Value)
            .Select(g => new PunchEmployeeDto(
                g.Key, g.First().EmployeeNumber!, g.First().EmployeeName!, g.First().Number!,
                g.Count(),
                g.Select(r => r.At![..10]).Distinct().Count(),
                g.Min(r => r.At!)!, g.Max(r => r.At!)!))
            .OrderBy(e => e.EmployeeNumber, StringComparer.Ordinal)
            .ToList();

        var unknown = rows.Where(r => r.Status == "unknown")
            .GroupBy(r => r.Number!)
            .Select(g => new PunchUnknownNumberDto(g.Key, g.Count(),
                g.Select(r => r.Name).Where(n => n is not null).Select(n => n!).Distinct().Take(3).ToList()))
            .OrderByDescending(u => u.Rows).ThenBy(u => u.Number, StringComparer.Ordinal)
            .ToList();

        var elsewhere = rows.Where(r => r.Status == "otherBranch")
            .GroupBy(r => r.Number!)
            .Select(g => new PunchOtherBranchNumberDto(g.Key, g.Count()))
            .OrderBy(o => o.Number, StringComparer.Ordinal)
            .ToList();

        var shown = rows.Where(r => r.Status != "matched").Take(ProblemRowsShown)
            .Concat(rows.Where(r => r.Status == "matched").Take(MatchedRowsShown))
            .OrderBy(r => r.Row)
            .ToList();

        return HrResult<PunchImportPreviewDto>.Success(new PunchImportPreviewDto(
            fileName, sheet.Name, sheet.SheetNames, header, suggested, order, columns, top,
            null, summary, shown, shown.Count < rows.Count, employees, unknown, elsewhere, suggestedDto));
    }

    /// <summary>
    /// The columns that hold anything at or below the header, each with its header text and the
    /// first few values under it — what a person needs to say which column is which.
    /// </summary>
    private static List<PunchColumnDto> Columns(Worksheet sheet, int? header)
    {
        var headerRow = header is int h ? sheet.Rows.FirstOrDefault(r => r.Number == h) : null;
        var body = sheet.Rows.Where(r => header is not int hr || r.Number > hr).ToList();
        var indexes = body.SelectMany(r => r.Cells.Keys)
            .Concat(headerRow?.Cells.Keys ?? Enumerable.Empty<int>())
            .Where(i => i <= MaxColumns)
            .Distinct()
            .Order()
            .ToList();

        return indexes.Select(i => new PunchColumnDto(
            i, SheetColumns.Letter(i),
            headerRow?[i]?.Text?.Trim() is { Length: > 0 } text ? text : null,
            body.Select(r => r[i]?.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Take(3).Select(t => t!).ToList()))
            .ToList();
    }

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
