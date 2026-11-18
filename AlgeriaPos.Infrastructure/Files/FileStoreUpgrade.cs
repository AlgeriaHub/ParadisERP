using System.Data.Common;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Application.Files;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlgeriaPos.Infrastructure.Files;

/// <summary>
/// Brings a company database up to date, moving the files it used to hold into the files folder
/// on the way (owner, 2026-10-04: only the path is stored in the database).
///
/// <para>
/// Two migrations, back to back: <see cref="PathsMigration"/> adds the path columns beside the
/// logo data URLs and the attachment bytes, and <see cref="DropMigration"/> drops the old ones. A
/// migration cannot write a file, so while the second is pending the database is migrated to the
/// first, every file is written out and its path recorded, and only then is the rest applied. A
/// start that dies half-way repeats the move where it stopped: a row is done once its path is set.
/// </para>
/// <para>
/// Every migration of a company database goes through here — the host's start and the portal's
/// restore of an archived company — never straight through <c>MigrateAsync</c>, or a database
/// with files in it would lose them to the drop.
/// </para>
/// </summary>
public static class FileStoreUpgrade
{
    public const string PathsMigration = "FileStorePaths";
    public const string DropMigration = "FilesOutOfDatabase";

    public static async Task MigrateAsync(PosDbContext db, IFileStore files, CancellationToken ct = default)
    {
        var pending = await db.Database.GetPendingMigrationsAsync(ct);
        if (pending.Any(m => m.EndsWith("_" + DropMigration, StringComparison.Ordinal)))
        {
            // Safe as a target only because the two are consecutive: a database still owing the
            // drop is at or before the paths migration, so this never rolls anything back.
            await db.GetService<IMigrator>().MigrateAsync(PathsMigration, ct);
            await MoveOutAsync(db, files, ct);
        }
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Writes out every logo and attachment still held in a row, and records its path. Plain SQL,
    /// because the model no longer has the old columns; the ids are passed back exactly as read,
    /// so each provider's own representation of a Guid is never reinterpreted.
    /// </summary>
    internal static async Task MoveOutAsync(PosDbContext db, IFileStore files, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            foreach (var table in new[] { "Stores", "Branches" })
            {
                foreach (var id in await IdsAsync(connection, $"SELECT Id FROM {table} WHERE LogoDataUrl IS NOT NULL AND LogoPath IS NULL", ct))
                {
                    var dataUrl = (string?)await ScalarAsync(connection, $"SELECT LogoDataUrl FROM {table} WHERE Id = @id", id, ct);
                    // Every stored logo passed the same reading when it was saved; one that no
                    // longer reads is dropped rather than left to block the upgrade.
                    if (dataUrl is null || StoreLogo.TryRead(dataUrl, int.MaxValue / 2, out var logo) is not null || logo is null)
                        continue;
                    await files.WriteAsync(logo.Path, logo.Content, ct);
                    await ExecuteAsync(connection, $"UPDATE {table} SET LogoPath = @path WHERE Id = @id", id, logo.Path, ct);
                }
            }

            foreach (var id in await IdsAsync(connection, "SELECT Id FROM RentalContractAttachments WHERE StoragePath = ''", ct))
            {
                await using var read = Command(connection, "SELECT ContractId, ContentType, Content FROM RentalContractAttachments WHERE Id = @id", id);
                string path;
                byte[] content;
                await using (var reader = await read.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct)) continue;
                    var contractId = reader.GetValue(0) is Guid g ? g : Guid.Parse(Convert.ToString(reader.GetValue(0))!);
                    var contentType = reader.GetString(1);
                    content = (byte[])reader.GetValue(2);
                    path = FilePaths.ContractAttachment(contractId, AsGuid(id), contentType);
                }
                await files.WriteAsync(path, content, ct);
                await ExecuteAsync(connection, "UPDATE RentalContractAttachments SET StoragePath = @path WHERE Id = @id", id, path, ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static Guid AsGuid(object id) => id is Guid g ? g : Guid.Parse(Convert.ToString(id)!);

    // Ids first, then one row at a time: a provider may not run a second command while a reader is open.
    private static async Task<List<object>> IdsAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var ids = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetValue(0));
        return ids;
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, object id, CancellationToken ct)
    {
        await using var command = Command(connection, sql, id);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, object id, string path, CancellationToken ct)
    {
        await using var command = Command(connection, sql, id);
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@path";
        parameter.Value = path;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand Command(DbConnection connection, string sql, object id)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        return command;
    }
}
