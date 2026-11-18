using Microsoft.EntityFrameworkCore.Migrations;

namespace AlgeriaPos.Infrastructure.Persistence;

/// <summary>
/// Puts a customer on the ledger lines that already had one and could not say so.
///
/// <para>
/// <b>Why this exists.</b> <c>LedgerEntryLine.CustomerId</c> arrives nullable, and a nullable
/// column added to a table with rows in it defaults to null on every one of them. Left there,
/// account 411 would carry a customer's whole trading history with nobody's name against any of
/// it: every statement would open at zero, every ageing report would show a contractor owing
/// only what they had been billed since the upgrade, and nothing anywhere would fail. That is
/// the fourth bug of exactly this shape in this repository, so it is fixed here rather than
/// found later.
/// </para>
/// <para>
/// Every posting that has ever touched 411 or 413 came from a document that knows whose debt it
/// is — a sale, a cheque settled against one, a rental note. The attribution is recovered from
/// those documents rather than guessed, and a sale with no customer stays null, which is
/// correct: nobody owed it.
/// </para>
/// <para>
/// One correlated <c>UPDATE</c> per source. Only the SQLite migration set runs it: the SQL Server
/// set starts from the current schema, with no ledger to backfill. It touches
/// only rows whose customer is still unknown and whose entry came from that source, so the
/// statements cannot undo one another and re-running the migration cannot double anything.
/// </para>
/// </summary>
public static class LedgerCustomerBackfill
{
    /// <summary>SQLite quotes identifiers with double quotes.</summary>
    public const string Sqlite = "\"\"";

    public static void Run(MigrationBuilder migrationBuilder, string quoting)
    {
        foreach (var statement in Statements(quoting)) migrationBuilder.Sql(statement);
    }

    public static IEnumerable<string> Statements(string quoting)
    {
        string Q(string name) => $"{quoting[0]}{name}{quoting[1]}";

        // A sale carries its customer directly. Both the trade-debt line and the uncleared
        // cheque line belong to them.
        yield return Update(Q, "sale", $"""
            SELECT s.{Q("CustomerId")}
            FROM {Q("LedgerEntries")} e
            INNER JOIN {Q("Sales")} s ON s.{Q("Id")} = e.{Q("SourceId")}
            WHERE e.{Q("Id")} = {Q("LedgerEntryLines")}.{Q("EntryId")} AND e.{Q("SourceType")} = 'sale'
            """);

        // A cheque clearing or bouncing: the paper leads back to the sale it was taken on.
        yield return Update(Q, "payment-settlement", $"""
            SELECT s.{Q("CustomerId")}
            FROM {Q("LedgerEntries")} e
            INNER JOIN {Q("SalePayments")} p ON p.{Q("Id")} = e.{Q("SourceId")}
            INNER JOIN {Q("Sales")} s ON s.{Q("Id")} = p.{Q("SaleId")}
            WHERE e.{Q("Id")} = {Q("LedgerEntryLines")}.{Q("EntryId")} AND e.{Q("SourceType")} = 'payment-settlement'
            """);

        // Goods going out and coming back: the customer is on the contract, not on the note.
        yield return Update(Q, "rental-issue", $"""
            SELECT c.{Q("CustomerId")}
            FROM {Q("LedgerEntries")} e
            INNER JOIN {Q("IssueNotes")} n ON n.{Q("Id")} = e.{Q("SourceId")}
            INNER JOIN {Q("RentalContracts")} c ON c.{Q("Id")} = n.{Q("ContractId")}
            WHERE e.{Q("Id")} = {Q("LedgerEntryLines")}.{Q("EntryId")} AND e.{Q("SourceType")} = 'rental-issue'
            """);

        yield return Update(Q, "rental-return", $"""
            SELECT c.{Q("CustomerId")}
            FROM {Q("LedgerEntries")} e
            INNER JOIN {Q("ReturnNotes")} n ON n.{Q("Id")} = e.{Q("SourceId")}
            INNER JOIN {Q("RentalContracts")} c ON c.{Q("Id")} = n.{Q("ContractId")}
            WHERE e.{Q("Id")} = {Q("LedgerEntryLines")}.{Q("EntryId")} AND e.{Q("SourceType")} = 'rental-return'
            """);
    }

    /// <summary>
    /// The one shape all four statements take: set the customer from a correlated lookup, on the
    /// third-party lines of that source only.
    ///
    /// <para>
    /// The second condition is what makes the four safe to run in sequence — without it the
    /// third statement would overwrite the first two with the null its own lookup returns for a
    /// sale. Account codes rather than ids, because ids are generated per company and the codes
    /// are what the whole system posts by.
    /// </para>
    /// </summary>
    private static string Update(Func<string, string> q, string sourceType, string lookup) => $"""
        UPDATE {q("LedgerEntryLines")}
        SET {q("CustomerId")} = ({lookup})
        WHERE {q("CustomerId")} IS NULL
          AND {q("AccountId")} IN (
              SELECT {q("Id")} FROM {q("LedgerAccounts")} WHERE {q("Code")} IN ('411', '413'))
          AND {q("EntryId")} IN (
              SELECT {q("Id")} FROM {q("LedgerEntries")} WHERE {q("SourceType")} = '{sourceType}');
        """;
}
