using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Schemora.Workspaces;

public sealed record CatalogRow(string Table, string Id, string? WorkspaceId, DateTimeOffset UpdatedAt, long Bytes);

public partial class CatalogStore
{
    public IReadOnlyList<object> SnapshotHeaders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.Id,s.WorkspaceId,json_extract(s.Json,'$.databaseId'),json_extract(s.Json,'$.fingerprint'),json_extract(s.Json,'$.buildManifestId'),json_extract(s.Json,'$.analysisVersion') FROM SchemaSnapshots s JOIN Databases d ON s.Id=json_extract(d.Json,'$.currentSnapshotId') LIMIT 500";
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read())
            result.Add(
                new
                {
                    SnapshotId = reader.GetString(0),
                    WorkspaceId = reader.GetString(1),
                    DatabaseId = reader.GetString(2),
                    Fingerprint = reader.GetString(3),
                    BuildManifestId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    AnalysisVersion = reader.IsDBNull(5) ? 1 : reader.GetInt32(5),
                }
            );
        return result;
    }

    // Enumerates metadata without deserializing historical schema graphs.
    public IReadOnlyList<CatalogRow> Inventory(string table, string? workspaceId = null)
    {
        ValidateTable(table);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT Id,WorkspaceId,UpdatedAt,length(CAST(Json AS BLOB)) FROM {table}"
            + (workspaceId is null ? "" : " WHERE WorkspaceId=$ws");
        if (workspaceId is not null)
            command.Parameters.AddWithValue("$ws", workspaceId);
        using var reader = command.ExecuteReader();
        var rows = new List<CatalogRow>();
        while (reader.Read())
            rows.Add(
                new(
                    table,
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2)),
                    reader.GetInt64(3)
                )
            );
        return rows;
    }

    public IReadOnlyList<T> Recent<T>(string table, int limit, string? workspaceId = null)
    {
        ValidateTable(table);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT Json FROM {table}"
            + (workspaceId is null ? "" : " WHERE WorkspaceId=$ws")
            + " ORDER BY UpdatedAt DESC,Id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        if (workspaceId is not null)
            command.Parameters.AddWithValue("$ws", workspaceId);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            if (JsonSerializer.Deserialize<T>(reader.GetString(0), Json) is { } item)
                result.Add(item);
        return result;
    }

    public void DeleteHistory(IReadOnlyList<(string Table, string Id)> records, IReadOnlyList<string> snapshotIds)
    {
        lock (writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var (table, id) in records)
            {
                ValidateTable(table);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE Id=$id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            foreach (var id in snapshotIds)
            foreach (var table in new[] { "SchemaObjects", "Columns", "Relations", "RelationEvidence" })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE json_extract(Json,'$.snapshotId')=$id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
            checkpoint.ExecuteNonQuery();
        }
    }
}
