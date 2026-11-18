using System.Text;
using ParadisERP.Application.Files;
using ParadisERP.Infrastructure.Files;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadisERP.Tests.Files;

/// <summary>
/// The upgrade as an installation that already holds files performs it (owner, 2026-10-04: files
/// in a folder, only the path in the database). The real migrations run on a real SQLite database
/// with logos and a contract scan in the previous release's columns; every one of them must reach
/// the files folder, with its path recorded, before the migration that drops those columns — and a
/// second start changes nothing.
/// </summary>
public class FileStoreUpgradeTests
{
    /// <summary>The release before the files left the database.</summary>
    private const string Before = "AuthorizationVersions";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0 L1 1\"/></svg>";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    // The way EF writes a Guid on SQLite, so its own queries find these rows afterwards.
    private static string Key(Guid id) => id.ToString().ToUpperInvariant();

    [Fact]
    public async Task Logos_and_scans_held_in_rows_reach_the_files_folder_before_their_columns_are_dropped()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        PosDbContext Open() => new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await using (var old = Open()) await old.Database.GetService<IMigrator>().MigrateAsync(Before);
        // Only the rows the move reads are written; the contract and branch they would hang off are not.
        await using (var off = connection.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys = OFF"; await off.ExecuteNonQueryAsync(); }

        Guid store = Guid.NewGuid(), branch = Guid.NewGuid(), bare = Guid.NewGuid();
        Guid contract = Guid.NewGuid(), scan = Guid.NewGuid();
        await InsertAsync(connection, "Stores", new() { ["Id"] = Key(store), ["LogoDataUrl"] = "data:image/png;base64," + Convert.ToBase64String(Png) });
        await InsertAsync(connection, "Branches", new() { ["Id"] = Key(branch), ["LogoDataUrl"] = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Svg)) });
        await InsertAsync(connection, "Branches", new() { ["Id"] = Key(bare) });
        await InsertAsync(connection, "RentalContractAttachments", new()
        {
            ["Id"] = Key(scan), ["ContractId"] = Key(contract), ["Kind"] = "idDocument", ["FileName"] = "permis.jpg",
            ["ContentType"] = "image/jpeg", ["Length"] = Jpeg.Length, ["Content"] = Jpeg,
        });

        var files = TestFiles.Fresh();
        await using (var upgrade = Open())
        {
            await FileStoreUpgrade.MigrateAsync(upgrade, files);
            Assert.Empty(await upgrade.Database.GetPendingMigrationsAsync());
        }

        await using var read = Open();
        var storeLogo = await read.Stores.Where(s => s.Id == store).Select(s => s.LogoPath).SingleAsync();
        Assert.Equal(FilePaths.Logo(Png, "image/png"), storeLogo);
        Assert.Equal(Png, await files.ReadAllAsync(storeLogo!));

        var branchLogo = await read.Branches.Where(b => b.Id == branch).Select(b => b.LogoPath).SingleAsync();
        Assert.EndsWith(".svg", branchLogo);
        Assert.Equal(Svg, Encoding.UTF8.GetString((await files.ReadAllAsync(branchLogo!))!));
        Assert.Null(await read.Branches.Where(b => b.Id == bare).Select(b => b.LogoPath).SingleAsync());

        var scanPath = await read.RentalContractAttachments.Where(a => a.Id == scan).Select(a => a.StoragePath).SingleAsync();
        Assert.Equal(FilePaths.ContractAttachment(contract, scan, "image/jpeg"), scanPath);
        Assert.Equal(Jpeg, await files.ReadAllAsync(scanPath));

        // The bytes are gone from the database itself.
        Assert.DoesNotContain("LogoDataUrl", await ColumnsAsync(connection, "Stores"));
        Assert.DoesNotContain("LogoDataUrl", await ColumnsAsync(connection, "Branches"));
        Assert.DoesNotContain("Content", await ColumnsAsync(connection, "RentalContractAttachments"));

        // A second start finds nothing to do.
        await using (var again = Open()) await FileStoreUpgrade.MigrateAsync(again, files);
        Assert.Equal(storeLogo, await read.Stores.Where(s => s.Id == store).Select(s => s.LogoPath).SingleAsync());
    }

    [Fact]
    public async Task A_new_database_is_migrated_with_nothing_to_move()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        var root = TestFiles.NewRoot();
        await FileStoreUpgrade.MigrateAsync(db, LocalFileStore.ForCompany(root, "default"));

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(Directory.Exists(root), "nothing is written when there is nothing to move");
    }

    /// <summary>
    /// A row in the old shape. Raw SQL, because the current model no longer has the old columns;
    /// every required column not named is given its type's empty value.
    /// </summary>
    private static async Task InsertAsync(SqliteConnection connection, string table, Dictionary<string, object?> values)
    {
        var row = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var (name, type, notNull, hasDefault) = (reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1, !reader.IsDBNull(4));
                if (!notNull || hasDefault || row.ContainsKey(name)) continue;
                row[name] = type.ToUpperInvariant() switch
                {
                    "INTEGER" => 0L,
                    "REAL" => 0.0,
                    "BLOB" => Array.Empty<byte>(),
                    _ => ""
                };
            }
        }
        await using var insert = connection.CreateCommand();
        var names = row.Keys.ToList();
        insert.CommandText = $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => "$p" + i))})";
        for (var i = 0; i < names.Count; i++) insert.Parameters.AddWithValue("$p" + i, row[names[i]] ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await info.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        return names;
    }
}
