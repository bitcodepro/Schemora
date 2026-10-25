using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record RecoveryPreview(
    string Id,
    string StagedPath,
    string Sha256,
    string CatalogRevision,
    int CatalogVersion,
    long Workspaces,
    long Recipes,
    long Snapshots,
    DateTimeOffset CreatedAt
);

public sealed record RecoveryPathRequest(string Path);

public sealed record RecoveryApplyRequest(string PreviewId, bool Confirmed, bool NoBrowser = false);

public static class CatalogCare
{
    public static string Revision(CatalogStore store) =>
        Identity.Hash(
            JsonSerializer.Serialize(
                new[]
                {
                    "Workspaces",
                    "Sources",
                    "Databases",
                    "SchemaSnapshots",
                    "Recipes",
                    "RecipeVersions",
                    "RelationOverrides",
                    "UserSettings",
                    "AnalysisReceipts",
                    "RecipeBatchRuns",
                }.Select(t => new
                {
                    Table = t,
                    Rows = store
                        .Inventory(t)
                        .Where(r => !r.Id.StartsWith("recovery:", StringComparison.Ordinal))
                        .Select(r => new
                        {
                            r.Id,
                            r.UpdatedAt,
                            r.Bytes,
                        })
                        .OrderBy(r => r.Id, StringComparer.Ordinal),
                }),
                CatalogStore.Json
            )
        );

    public static object Integrity(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(20)";
        var checks = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                checks.Add(reader.GetString(0));
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        return new
        {
            Healthy = checks.SequenceEqual(["ok"]),
            Checks = checks,
            CatalogVersion = version,
            SupportedVersion = 2,
        };
    }

    public static RecoveryPreview Preview(CatalogStore store, string dataDirectory, string sourcePath) =>
        store.WithWriteLock(() =>
        {
            var path = LocalFiles.Validate(sourcePath, ".sqlite");
            using var source = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString()
            );
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = "PRAGMA integrity_check(20)";
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                    throw new AppException("RECOVERY_INVALID", "The backup failed its integrity check.");
            }
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version is < 1 or > 2)
                throw new AppException("RECOVERY_VERSION_UNSUPPORTED", "This backup catalog version is not supported.");
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Workspaces','Recipes','SchemaSnapshots','UserSettings')";
            if (Convert.ToInt32(command.ExecuteScalar()) != 4)
                throw new AppException("RECOVERY_INVALID", "The file is not a Schemora catalog.");
            long Count(string table)
            {
                command.CommandText = "SELECT COUNT(*) FROM " + table;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            var ws = Count("Workspaces");
            var recipes = Count("Recipes");
            var snapshots = Count("SchemaSnapshots");
            var id = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(dataDirectory, "recovery-previews");
            Directory.CreateDirectory(folder);
            var staged = Path.Combine(folder, id + ".sqlite");
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staged, Pooling = false }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
            }
            var plan = new RecoveryPreview(
                id,
                staged,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged))),
                Revision(store),
                version,
                ws,
                recipes,
                snapshots,
                DateTimeOffset.UtcNow
            );
            store.Put("UserSettings", "recovery:" + id, null, plan);
            return plan;
        });

    public static RecoveryPreview Validate(CatalogStore store, RecoveryApplyRequest request, JobQueue jobs) =>
        store.WithWriteLock(() =>
        {
            if (!request.Confirmed)
                throw new AppException("RECOVERY_CONFIRMATION_REQUIRED", "Review the restore preview and confirm the application restart.");
            if (jobs.HasActiveJobs)
                throw new AppException("RECOVERY_BUSY", "Wait for active jobs before restoring.", 409);
            var plan =
                store.Get<RecoveryPreview>("UserSettings", "recovery:" + request.PreviewId)
                ?? throw new AppException("RECOVERY_PREVIEW_REQUIRED", "Preview the backup first.");
            if (DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromMinutes(30) || plan.CatalogRevision != Revision(store))
                throw new AppException("RECOVERY_CONTEXT_CHANGED", "The catalog changed. Preview the backup again.", 409);
            LocalFiles.Validate(plan.StagedPath, ".sqlite");
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plan.StagedPath))) != plan.Sha256)
                throw new AppException("RECOVERY_BACKUP_CHANGED", "The staged backup changed. Preview again.", 409);
            return plan;
        });

    public static void MapCatalogCare(this WebApplication app, string dataDirectory)
    {
        app.MapGet(
            "/api/storage/recovery/backups",
            () =>
                new[] { Path.Combine(dataDirectory, "backups"), dataDirectory }
                    .Where(Directory.Exists)
                    .SelectMany(folder => Directory.EnumerateFiles(folder, "*.sqlite", SearchOption.TopDirectoryOnly))
                    .Where(p =>
                        Path.GetDirectoryName(p) != dataDirectory
                        || Path.GetFileName(p).StartsWith("before-restore-", StringComparison.Ordinal)
                    )
                    .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                    .Select(p => new
                    {
                        Path = p,
                        Name = Path.GetFileName(p),
                        Bytes = new FileInfo(p).Length,
                        CreatedAt = File.GetLastWriteTimeUtc(p),
                    })
                    .OrderByDescending(p => p.CreatedAt)
                    .Take(100)
        );
        app.MapGet("/api/storage/integrity", (CatalogStore store) => store.WithWriteLock(() => Integrity(store.Path)));
        app.MapPost(
            "/api/storage/recovery/preview",
            (RecoveryPathRequest request, CatalogStore store) => Preview(store, dataDirectory, request.Path)
        );
        app.MapPost(
            "/api/storage/recovery/apply",
            (RecoveryApplyRequest request, CatalogStore store, JobQueue jobs, HttpContext context, IHostApplicationLifetime lifetime) =>
            {
                var plan = Validate(store, request, jobs);
                var executable =
                    Environment.ProcessPath
                    ?? throw new AppException("RECOVERY_RESTART_UNAVAILABLE", "Restart is unavailable in this host.");
                var info = new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                    info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Schemora.Host.dll"));
                foreach (
                    var argument in new[]
                    {
                        "--data-dir",
                        dataDirectory,
                        "--restore-backup",
                        plan.StagedPath,
                        "--wait-for-process",
                        Environment.ProcessId.ToString(),
                    }
                )
                    info.ArgumentList.Add(argument);
                if (request.NoBrowser)
                    info.ArgumentList.Add("--no-browser");
                context.Response.OnCompleted(() =>
                {
                    System.Diagnostics.Process.Start(info)?.Dispose();
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
                return Results.Ok(new { Status = "restarting", PreviousCopyPreserved = true });
            }
        );
    }
}
