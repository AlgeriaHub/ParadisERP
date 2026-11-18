using System.Globalization;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Catalog;

/// <summary>
/// The codes the server draws for a new customer and a new employee (owner, 2026-10-05), written
/// down in this one place. Never typed, never changed afterwards.
///
/// <para>
/// <b>Shape: prefix + branch code + YYMM + sequence</b>, one dash only, after the prefix:
/// </para>
/// <list type="bullet">
/// <item><b>prefix</b> — <c>Cust-</c> a natural person, <c>Comp-</c> a company, <c>Emp-</c> an employee;</item>
/// <item><b>branch code</b> — the three digits of the location the record was created at
/// (<see cref="BranchCodes"/>: 100–999, never a leading zero);</item>
/// <item><b>YYMM</b> — the year and month it was created, read in the <i>company's</i> time zone (the
/// regional profile's), never the server's clock or UTC: Africa/Algiers is an hour ahead, so 00:30
/// on 1 November there is a November record although UTC still says October;</item>
/// <item><b>sequence</b> — counts up from 001 within one prefix, one branch and one month, three
/// digits zero-padded, and past 999 it simply grows (999, then 1000) — it never fails.</item>
/// </list>
/// <para>
/// The owner's examples, branch 11, October 2026: persons <c>Cust-112610001</c>, <c>Cust-112610002</c>;
/// companies <c>Comp-112610001</c> on their own counter; employees <c>Emp-112610001</c>. The first
/// person of November at the same branch is <c>Cust-112611001</c>; after <c>Cust-112610999</c> comes
/// <c>Cust-1126101000</c>.
/// </para>
/// <para>
/// <b>Next = one past the highest sequence already carried</b> by a code with exactly that prefix,
/// that branch code and that YYMM — deleted rows included, so a number handed out once is never
/// handed out again. A code of any other shape (the legacy <c>C001</c> or <c>EMP-000001</c>, a
/// lower-case prefix, a four-digit sequence with a leading zero) is not in this series and is ignored,
/// never parsed loosely. Records from before the rule keep the codes they have — printed statements,
/// invoices and payslips carry them — and a record keeps its code when its kind or branch changes.
/// </para>
/// </summary>
public static class CodeSeries
{
    /// <summary>A natural person's series.</summary>
    public const string PersonPrefix = "Cust-";

    /// <summary>A company's series.</summary>
    public const string CompanyPrefix = "Comp-";

    /// <summary>An employee's series.</summary>
    public const string EmployeePrefix = "Emp-";

    /// <summary>The sequence is zero-padded to this many digits; more appear only past 999.</summary>
    public const int SequenceWidth = 3;

    /// <summary>
    /// Far more than any month can use, and short enough that parsing cannot overflow an int:
    /// a longer tail is not a sequence this class wrote.
    /// </summary>
    private const int MaxSequenceDigits = 9;

    /// <summary>The series a new customer of this kind is numbered in.</summary>
    /// <exception cref="ArgumentOutOfRangeException">For an unspecified kind: a new customer says what it is first.</exception>
    public static string CustomerPrefix(CustomerKind kind) => kind switch
    {
        CustomerKind.Person => PersonPrefix,
        CustomerKind.Company => CompanyPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A new customer is a person or a company before it is numbered."),
    };

    /// <summary>The YYMM of an instant in the company's own calendar.</summary>
    public static string Month(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(now, zone).ToString("yyMM", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Prefix, branch code and month: what every code of one counter starts with, e.g.
    /// <c>Stem("Cust-", "11", "2610")</c> is <c>Cust-112610</c>.
    /// </summary>
    /// <exception cref="ArgumentException">When the branch code is not three digits without a leading zero.</exception>
    public static string Stem(string prefix, string branchCode, string yymm)
    {
        if (!BranchCodes.IsWellFormed(branchCode))
            throw new ArgumentException($"'{branchCode}' is not a branch code (100–999).", nameof(branchCode));
        return prefix + branchCode + yymm;
    }

    /// <summary>The code itself, e.g. <c>Format("Cust-112610", 1)</c> is <c>Cust-112610001</c>.</summary>
    public static string Format(string stem, int sequence) =>
        stem + sequence.ToString("D" + SequenceWidth, CultureInfo.InvariantCulture);

    /// <summary>
    /// The sequence a code carries in this counter, or null when it is not one of its codes: it must
    /// start with the stem exactly (ordinal, case included) and continue with three digits, or with
    /// more digits whose first is not 0 — the only tails <see cref="Format"/> ever writes.
    /// </summary>
    public static int? SequenceOf(string? code, string stem)
    {
        if (code is null || !code.StartsWith(stem, StringComparison.Ordinal)) return null;
        var tail = code.AsSpan(stem.Length);
        if (tail.Length < SequenceWidth || tail.Length > MaxSequenceDigits) return null;
        if (tail.Length > SequenceWidth && tail[0] == '0') return null;
        foreach (var c in tail)
            if (!char.IsAsciiDigit(c)) return null;
        return int.Parse(tail, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>One past the highest sequence among <paramref name="codes"/> in this counter; 1 for a new month.</summary>
    public static int NextSequence(IEnumerable<string?> codes, string stem)
    {
        var highest = 0;
        foreach (var code in codes)
            if (SequenceOf(code, stem) is int n && n > highest) highest = n;
        return highest + 1;
    }
}
