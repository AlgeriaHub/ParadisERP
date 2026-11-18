using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadisERP.Tests.Persistence;

/// <summary>
/// The upgrade for `ContractWordings` (owner, PinSay #302: several named rental-contract wordings) —
/// the shape of <c>EmployeeEmailMigrationTests</c>. Two nullable columns and nothing else: a company
/// already on file has no named wordings and every contract prints the standard one, as before; and
/// the new list round-trips through its JSON column.
/// </summary>
public class ContractWordingsMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "BranchCodes";

    [Fact]
    public async Task A_company_on_file_has_no_named_wordings_and_the_list_round_trips()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);
        // The store row in the old shape, written with raw SQL: the current model names a column the
        // old table does not have. The table is wide, so every required column is read off the old
        // schema and given a plain value rather than listed here by hand. Parameters, never text.
        var storeId = Guid.NewGuid();
        var required = new List<(string Name, string Type)>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(Stores)";
            await using var reader = await schema.ExecuteReaderAsync();
            // cid, name, type, notnull, dflt_value, pk
            while (await reader.ReadAsync())
                if (reader.GetInt32(3) == 1 && reader.IsDBNull(4)) required.Add((reader.GetString(1), reader.GetString(2)));
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO Stores ({string.Join(", ", required.Select(c => c.Name))}) " +
                                 $"VALUES ({string.Join(", ", required.Select((_, i) => "$p" + i))})";
            // Each value from the column's type in the model, so a decimal or an instant SQLite keeps
            // as text is still one the model can read back.
            var columns = db.Model.FindEntityType(typeof(Store))!.GetProperties()
                .ToDictionary(p => p.GetColumnName(), p => Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType);
            for (var i = 0; i < required.Count; i++)
            {
                var name = required[i].Name;
                var clr = columns.GetValueOrDefault(name, typeof(string));
                object value = name switch
                {
                    "Id" => storeId.ToString().ToUpperInvariant(),
                    "NameFr" => "Béton SARL",
                    _ when clr == typeof(string) => "",
                    _ when clr == typeof(decimal) => "0.0",
                    _ when clr == typeof(DateTime) => "2026-01-01 00:00:00",
                    _ when clr == typeof(Guid) => Guid.Empty.ToString().ToUpperInvariant(),
                    // A JSON map (the fiscal identifiers): empty.
                    _ when clr.IsGenericType => "{}",
                    _ => 0,
                };
                insert.Parameters.AddWithValue("$p" + i, value);
            }
            await insert.ExecuteNonQueryAsync();
        }

        // The upgrade itself. A statement that is not valid SQL on this provider stops here.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var store = await db.Stores.SingleAsync(s => s.Id == storeId);
        Assert.Null(store.ContractWordings);
        Assert.Equal("Béton SARL", store.NameFr);

        store.ContractWordings = [new ContractWording { Key = "rentalContract.scaf01", Names = new() { ["fr"] = "Échafaudage", ["ar"] = "سقالات" } }];
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var wording = Assert.Single((await db.Stores.AsNoTracking().SingleAsync(s => s.Id == storeId)).ContractWordings!);
        Assert.Equal("rentalContract.scaf01", wording.Key);
        Assert.Equal("سقالات", wording.Names["ar"]);
    }
}
