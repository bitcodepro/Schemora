using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ManagedFilesTests
{
    [Fact]
    public void PreviewProtectsLiveCatalogReferencedManifestPackagesAndRecentBackupsAndRefusesStaleApply()
    {
        var directory = Path.Combine(Path.GetTempPath(), "managed-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            ManagedFiles.Save(store, new(1, 2));
            string FileAt(string folder, string name)
            {
                var target = Path.Combine(directory, folder);
                Directory.CreateDirectory(target);
                var path = Path.Combine(target, name);
                File.WriteAllText(path, "synthetic");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                return path;
            }
            var manifestId = Guid.NewGuid().ToString("N");
            var manifest = FileAt("build-manifests", manifestId + ".json");
            var orphan = FileAt("build-manifests", Guid.NewGuid().ToString("N") + ".json");
            var package = FileAt("nuget-packages", "offline.nupkg");
            var unknown = FileAt("backups", "my-important-backup.sqlite");
            for (int i = 0; i < 4; i++)
                FileAt("backups", "before-cleanup-" + Guid.NewGuid().ToString("N") + ".sqlite");
            store.Put(
                "SchemaSnapshots",
                "s",
                "ws",
                new SnapshotRecord(
                    "s",
                    "ws",
                    "db",
                    DateTimeOffset.UtcNow,
                    "rebuild",
                    "h",
                    new SchemaGraph("demo", "source.sql", DateTimeOffset.UtcNow, [], [], []),
                    manifestId
                )
            );
            var preview = ManagedFiles.Preview(store);
            Assert.False(preview.Files.Single(f => f.Path == manifest).Eligible);
            Assert.False(preview.Files.Single(f => f.Path == package).Eligible);
            Assert.False(preview.Files.Single(f => f.Path == unknown).Eligible);
            Assert.True(preview.Files.Single(f => f.Path == orphan).Eligible);
            Assert.Equal(2, preview.Files.Count(f => f.Category == "backup" && f.Eligible));
            File.WriteAllText(orphan, "changed");
            var jobs = new JobQueue(store, null!, null!);
            Assert.Equal("CLEANUP_CHANGED", Assert.Throws<AppException>(() => ManagedFiles.Apply(store, new(preview.Revision), jobs)).Code);
            Assert.True(File.Exists(orphan));
            var fresh = ManagedFiles.Preview(store);
            ManagedFiles.Apply(store, new(fresh.Revision), jobs);
            Assert.All(fresh.Files.Where(f => !f.Eligible), f => Assert.True(File.Exists(f.Path)));
            Assert.All(fresh.Files.Where(f => f.Eligible), f => Assert.False(File.Exists(f.Path)));
            Assert.True(File.Exists(store.Path));
            Assert.Throws<AppException>(() => ManagedFiles.Save(store, new(0, 0)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
