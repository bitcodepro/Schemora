using System.Text.Json;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record StoragePolicy(bool LowMemory = true, int KeepSnapshotsPerDatabase = 5, int KeepHistoryDays = 30);

public sealed record CleanupPlan(
    string Revision,
    int Snapshots,
    int HistoryRecords,
    long EstimatedBytes,
    IReadOnlyList<string> ProtectedSnapshotIds
);

public sealed record CleanupRequest(string ExpectedRevision);

public sealed class StorageMaintenance(CatalogStore store)
{
    private const string PolicyKey = "storage-policy";

    public StoragePolicy Policy() => store.Get<StoragePolicy>("UserSettings", PolicyKey) ?? new();

    public StoragePolicy Update(StoragePolicy policy)
    {
        if (policy.KeepSnapshotsPerDatabase is < 2 or > 100 || policy.KeepHistoryDays is < 1 or > 3650)
            throw new AppException("STORAGE_POLICY_INVALID", "Keep 2–100 snapshots per database and 1–3650 days of history.");
        store.Put("UserSettings", PolicyKey, null, policy);
        return policy;
    }

    private sealed record Selection(CleanupPlan Plan, IReadOnlyList<(string Table, string Id)> Records, IReadOnlyList<string> SnapshotIds);

    private Selection Select()
    {
        var policy = Policy();
        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
        var databases = store.All<DatabaseInfo>("Databases");
        foreach (var database in databases)
        {
            if (database.CurrentSnapshotId is { } id)
                protectedIds.Add(id);
            foreach (var snapshot in store.Snapshots(database.Id).Take(policy.KeepSnapshotsPerDatabase))
                protectedIds.Add(snapshot.Id);
        }
        foreach (var recipe in store.All<RecipeEntry>("Recipes"))
        foreach (var id in recipe.LastCompatibleSnapshots.Values)
            protectedIds.Add(id);
        // Keep every snapshot referenced by retained validation/repair history, including
        // archived recipes. Recipe versions and fixture definitions are never pruned.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.KeepHistoryDays);
        var records = new List<(string Table, string Id)>();
        var bytes = 0L;
        foreach (
            var table in new[]
            {
                "Jobs",
                "ScanRuns",
                "ValidationRuns",
                "RecipeRegressionRuns",
                "RepairProposals",
                "AnalysisReceipts",
                "RecipeBatchRuns",
            }
        )
        {
            foreach (var row in store.Inventory(table))
            {
                var document = store.Get<JsonElement>(table, row.Id);
                var active = document.TryGetProperty("status", out var status) && status.GetString() is "queued" or "running";
                var keep = row.UpdatedAt >= cutoff || active;
                // Unapplied repair previews may be inspected/approved later.
                if (
                    table == "RepairProposals"
                    && document.TryGetProperty("status", out var proposalStatus)
                    && proposalStatus.GetString() == "proposed"
                )
                    keep = true;
                if (!keep)
                {
                    records.Add((table, row.Id));
                    bytes += row.Bytes;
                    continue;
                }
                if (document.TryGetProperty("snapshots", out var snapshots) && snapshots.ValueKind == JsonValueKind.Object)
                    foreach (var entry in snapshots.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.String)
                            protectedIds.Add(entry.Value.GetString()!);
            }
        }
        var snapshotsToDelete = store
            .Inventory("SchemaSnapshots")
            .Where(r => r.UpdatedAt < cutoff && !protectedIds.Contains(r.Id))
            .ToArray();
        records.AddRange(snapshotsToDelete.Select(r => (r.Table, r.Id)));
        bytes += snapshotsToDelete.Sum(r => r.Bytes);
        var snapshotIds = snapshotsToDelete.Select(r => r.Id).Order(StringComparer.Ordinal).ToArray();
        var revision = Identity.Hash(
            JsonSerializer.Serialize(
                new
                {
                    policy,
                    Records = records.OrderBy(r => r.Table).ThenBy(r => r.Id),
                    Protected = protectedIds.Order(StringComparer.Ordinal),
                },
                CatalogStore.Json
            )
        );
        return new(
            new(
                revision,
                snapshotIds.Length,
                records.Count - snapshotIds.Length,
                bytes,
                protectedIds.Order(StringComparer.Ordinal).ToArray()
            ),
            records,
            snapshotIds
        );
    }

    public CleanupPlan Preview() => store.WithWriteLock(() => Select().Plan);

    public object Apply(CleanupRequest request, JobQueue jobs)
    {
        return store.WithWriteLock<object>(() =>
        {
            if (jobs.HasActiveJobs)
                throw new AppException("STORAGE_BUSY", "Wait for active jobs to finish before cleaning history.", 409);
            var selection = Select();
            if (selection.Plan.Revision != request.ExpectedRevision)
                throw new AppException("CLEANUP_CHANGED", "History or its retention policy changed. Preview cleanup again.", 409);
            if (selection.Records.Count == 0)
                return new { Deleted = 0, BackupCreated = false };
            var backups = Path.Combine(Path.GetDirectoryName(store.Path)!, "backups");
            Directory.CreateDirectory(backups);
            store.Backup(Path.Combine(backups, "before-cleanup-" + Guid.NewGuid().ToString("N") + ".sqlite"));
            store.DeleteHistory(selection.Records, selection.SnapshotIds);
            return new { Deleted = selection.Records.Count, BackupCreated = true };
        });
    }

    public object Size()
    {
        var tables = new[]
        {
            "SchemaSnapshots",
            "SchemaObjects",
            "Columns",
            "Relations",
            "Jobs",
            "ScanRuns",
            "ValidationRuns",
            "RecipeVersions",
            "RecipeRegressionRuns",
        };
        return new
        {
            CatalogBytes = new FileInfo(store.Path).Length,
            WalBytes = File.Exists(store.Path + "-wal") ? new FileInfo(store.Path + "-wal").Length : 0,
            Tables = tables
                .Select(table =>
                {
                    var rows = store.Inventory(table);
                    return new
                    {
                        Table = table,
                        Records = rows.Count,
                        Bytes = rows.Sum(r => r.Bytes),
                    };
                })
                .ToArray(),
        };
    }
}

public static class StorageMaintenanceEndpoints
{
    public static void MapStorageMaintenance(this WebApplication app)
    {
        app.MapGet("/api/storage/policy", (StorageMaintenance service) => service.Policy());
        app.MapPut(
            "/api/storage/policy",
            (StoragePolicy request, StorageMaintenance service, CatalogStore store, RelationshipService relationships) =>
            {
                var policy = service.Update(request);
                foreach (var ws in store.All<Workspace>("Workspaces"))
                    relationships.Invalidate(ws.Id);
                return policy;
            }
        );
        app.MapGet("/api/storage/size", (StorageMaintenance service) => service.Size());
        app.MapPost("/api/storage/cleanup/preview", (StorageMaintenance service) => service.Preview());
        app.MapPost(
            "/api/storage/cleanup",
            (CleanupRequest request, StorageMaintenance service, JobQueue jobs) => service.Apply(request, jobs)
        );
    }
}
