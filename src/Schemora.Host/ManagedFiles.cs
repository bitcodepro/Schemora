using System.Security.Cryptography;
using System.Text.Json;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record ManagedFilePolicy(int KeepDays = 30, int KeepBackups = 3);

public sealed record ManagedFile(
    string Path,
    string Category,
    long Bytes,
    DateTimeOffset ModifiedAt,
    bool Eligible,
    string Reason,
    string? Sha256
);

public sealed record ManagedFilePlan(
    string Revision,
    ManagedFilePolicy Policy,
    IReadOnlyList<ManagedFile> Files,
    long TotalBytes,
    long ReclaimableBytes
);

/// <summary>Only app-owned, allow-listed files inside the private data directory are eligible.</summary>
public static class ManagedFiles
{
    private const string PolicyKey = "managed-file-policy";

    public static ManagedFilePolicy Policy(CatalogStore store) => store.Get<ManagedFilePolicy>("UserSettings", PolicyKey) ?? new();

    public static ManagedFilePolicy Save(CatalogStore store, ManagedFilePolicy policy)
    {
        if (policy.KeepDays is < 1 or > 3650 || policy.KeepBackups is < 2 or > 100)
            throw new AppException("FILE_POLICY_INVALID", "Keep files for 1–3650 days and retain 2–100 backups.");
        store.Put("UserSettings", PolicyKey, null, policy);
        return policy;
    }

    public static ManagedFilePlan Preview(CatalogStore store)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(store.Path))!;
        LocalFiles.CheckNoLinks(root);
        var policy = Policy(store);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.KeepDays);
        var protectedManifests = new HashSet<string>(StringComparer.Ordinal);
        using (
            var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = store.Path,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                }.ToString()
            )
        )
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT json_extract(Json,'$.buildManifestId') FROM SchemaSnapshots WHERE json_extract(Json,'$.buildManifestId') IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                protectedManifests.Add(reader.GetString(0));
        }
        var files = new List<ManagedFile>();
        void Add(string path, string category, bool owned, string? protection = null)
        {
            if (files.Count >= 50000)
                throw new AppException("FILE_INVENTORY_LIMIT", "The managed file inventory exceeds 50000 entries. No files were removed.");
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new AppException("FILE_SCOPE_INVALID", "Managed file is outside the data directory.");
            LocalFiles.CheckNoLinks(full);
            var info = new FileInfo(full);
            var eligible = owned && protection is null && info.LastWriteTimeUtc < cutoff.UtcDateTime;
            var reason =
                protection
                ?? (
                    !owned ? "Not an owned cleanup target"
                    : eligible ? "Retention expired"
                    : "Within retention period"
                );
            string? hash = null;
            if (eligible)
            {
                using var input = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                hash = Convert.ToHexString(SHA256.HashData(input));
            }
            files.Add(new(full, category, info.Length, info.LastWriteTimeUtc, eligible, reason, hash));
        }
        var backupPaths = new List<string>();
        var backups = Path.Combine(root, "backups");
        if (Directory.Exists(backups))
        {
            LocalFiles.CheckNoLinks(backups);
            backupPaths.AddRange(Directory.EnumerateFiles(backups, "*.sqlite"));
        }
        backupPaths.AddRange(Directory.EnumerateFiles(root, "before-restore-*.sqlite"));
        var orderedBackups = backupPaths.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        bool OwnedBackup(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("before-cleanup-", StringComparison.Ordinal) && Guid.TryParseExact(name[15..], "N", out _)
                || name.StartsWith("before-restore-", StringComparison.Ordinal)
                    && name.Length == 62
                    && Guid.TryParseExact(name[^32..], "N", out _);
        }
        var protectedBackups = orderedBackups.Where(OwnedBackup).Take(policy.KeepBackups).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < orderedBackups.Length; i++)
        {
            var name = Path.GetFileNameWithoutExtension(orderedBackups[i]);
            var owned =
                name.StartsWith("before-cleanup-", StringComparison.Ordinal) && Guid.TryParseExact(name[15..], "N", out _)
                || name.StartsWith("before-restore-", StringComparison.Ordinal)
                    && name.Length == 62
                    && Guid.TryParseExact(name[^32..], "N", out _);
            Add(
                orderedBackups[i],
                "backup",
                owned,
                protectedBackups.Contains(orderedBackups[i]) ? "Recent backup protected"
                    : AutomaticBackups.IsAutomatic(Path.GetFileName(orderedBackups[i])) ? "Kept by the automatic backup schedule"
                    : null
            );
        }
        foreach (
            var (folder, category, extension) in new[]
            {
                ("build-manifests", "build", ".json"),
                ("recovery-previews", "temporary", ".sqlite"),
                ("docker-diagnostics", "diagnostic", ".json"),
            }
        )
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory))
                continue;
            LocalFiles.CheckNoLinks(directory);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                Add(
                    path,
                    category,
                    Path.GetExtension(path) == extension && Guid.TryParseExact(id, "N", out _),
                    folder == "build-manifests" && protectedManifests.Contains(id) ? "Referenced by a retained snapshot" : null
                );
            }
        }
        // Package caches are reported, but never deleted: even old packages may be needed offline.
        foreach (var folder in new[] { "nuget-packages", "builds" })
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory))
                continue;
            LocalFiles.CheckNoLinks(directory);
            var queue = new Queue<string>();
            queue.Enqueue(directory);
            var directories = 0;
            while (queue.TryDequeue(out var current))
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    LocalFiles.CheckNoLinks(entry);
                    if (Directory.Exists(entry))
                    {
                        if (++directories > 50000)
                            throw new AppException(
                                "FILE_INVENTORY_LIMIT",
                                "The managed file inventory exceeds 50000 directories. No files were removed."
                            );
                        queue.Enqueue(entry);
                    }
                    else
                    {
                        var stageName = Path.GetRelativePath(directory, entry).Split(Path.DirectorySeparatorChar)[0];
                        var stageRoot = Path.Combine(directory, stageName);
                        var orphan =
                            folder == "builds"
                            && Guid.TryParseExact(stageName, "N", out _)
                            && Directory.GetCreationTimeUtc(stageRoot) < cutoff.UtcDateTime
                            && Directory.GetLastWriteTimeUtc(stageRoot) < cutoff.UtcDateTime;
                        Add(
                            entry,
                            folder == "builds" ? "temporary" : "package",
                            orphan,
                            orphan ? null : "Build input or package cache protected"
                        );
                    }
                }
        }
        var ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var revision = Identity.Hash(JsonSerializer.Serialize(new { policy, files = ordered }, CatalogStore.Json));
        return new(revision, policy, ordered, ordered.Sum(f => f.Bytes), ordered.Where(f => f.Eligible).Sum(f => f.Bytes));
    }

    public static object Apply(CatalogStore store, CleanupRequest request, JobQueue jobs) =>
        store.WithWriteLock<object>(() =>
        {
            if (jobs.HasActiveJobs)
                throw new AppException("STORAGE_BUSY", "Wait for active jobs to finish before cleaning history.", 409);
            var plan = Preview(store);
            if (plan.Revision != request.ExpectedRevision)
                throw new AppException("CLEANUP_CHANGED", "Managed files or retention changed. Preview cleanup again.", 409);
            long deletedBytes = 0;
            var deleted = new List<string>();
            foreach (var file in plan.Files.Where(f => f.Eligible))
            {
                // All candidates are individually validated again. Refuse links or changed bytes.
                LocalFiles.CheckNoLinks(file.Path);
                using (var input = File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.None))
                    if (Convert.ToHexString(SHA256.HashData(input)) != file.Sha256)
                        throw new AppException("CLEANUP_CHANGED", "A cleanup file changed. Preview again.", 409);
                File.Delete(file.Path);
                deleted.Add(file.Path);
                deletedBytes += file.Bytes;
            }
            return new
            {
                Deleted = deleted.Count,
                DeletedBytes = deletedBytes,
                Paths = deleted,
            };
        });

    public static void MapManagedFiles(this WebApplication app)
    {
        app.MapGet("/api/storage/files", (CatalogStore store) => store.WithWriteLock(() => Preview(store)));
        app.MapPut(
            "/api/storage/files/policy",
            (ManagedFilePolicy request, CatalogStore store) => store.WithWriteLock(() => Save(store, request))
        );
        app.MapPost(
            "/api/storage/files/cleanup",
            (CleanupRequest request, CatalogStore store, JobQueue jobs) => Apply(store, request, jobs)
        );
    }
}
