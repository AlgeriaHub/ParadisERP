using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Sync;

/// <summary>
/// What a till is told when the server could not record a sale (docs/07 §2.4).
///
/// <para>
/// A client never receives database text. It gets a stable <see cref="Code"/> the till can
/// translate and a safe sentence; the exception itself goes to the server log under the
/// <see cref="Reference"/> the till also shows, so an operator can join the two. The
/// classification is by exception <b>type and error number</b>, never by matching words in a
/// provider's message — the old substring test on "Reference" would have called any error
/// that happened to mention the column a duplicate.
/// </para>
/// </summary>
public sealed record PushRejection(string Code, string Message, string? Reference)
{
    public const string DuplicateReceiptNumber = "duplicate_receipt_number";
    public const string ConcurrentUpdate = "concurrent_update";
    public const string ServerError = "server_error";

    /// <summary>
    /// SQLITE_CONSTRAINT — the primary code every constraint failure carries. On its own it
    /// also covers a foreign-key or NOT NULL failure, so the extended code is checked too.
    /// </summary>
    private const int SqliteConstraint = 19;
    /// <summary>SQLITE_CONSTRAINT_UNIQUE and SQLITE_CONSTRAINT_PRIMARYKEY.</summary>
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>SQL Server: a duplicate in a unique index (2601) or a primary key or unique constraint (2627).</summary>
    private const int SqlServerDuplicateIndex = 2601;
    private const int SqlServerDuplicateConstraint = 2627;

    /// <summary>
    /// Against the referenced packages (Microsoft.Data.Sqlite 8.0.11, Microsoft.Data.SqlClient via
    /// EF Core SQL Server 8.0.11): <c>SqliteException.SqliteErrorCode</c> /
    /// <c>SqliteExtendedErrorCode</c>, and <c>SqlException.Number</c>, the server's own error number.
    /// </summary>
    public static bool IsDuplicateKey(Exception ex) => Root(ex) switch
    {
        SqliteException s => s.SqliteErrorCode == SqliteConstraint
            && s.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey,
        SqlException m => m.Number is SqlServerDuplicateIndex or SqlServerDuplicateConstraint,
        _ => false,
    };

    public static PushRejection Classify(Exception ex)
    {
        if (ex is DbUpdateConcurrencyException)
            return new(ConcurrentUpdate, "Another terminal changed this record; the sale will be retried.", null);
        if (ex is DbUpdateException && IsDuplicateKey(ex))
            return new(DuplicateReceiptNumber, "Duplicate receipt number for this terminal — check the terminal's device code.", null);

        // The trace id when one is flowing (Production runs under the framework's activity), a
        // short random id otherwise — either way something an operator can grep the log for.
        var reference = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")[..12];
        return new(ServerError, $"The server could not record this sale. Reference {reference}.", reference);
    }

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }
}
