using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class CatalogCareTests
{
    [Fact]
    public void PreviewUsesSeparateVerifiedCopyAndLeavesCurrentCatalogUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "care-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            store.Put("Workspaces", "first", "first", new Workspace("first", "before", [], [], DateTimeOffset.UtcNow));
            var backup = Path.Combine(directory, "backup.sqlite");
            store.Backup(backup);
            store.Put("Workspaces", "later", "later", new Workspace("later", "after", [], [], DateTimeOffset.UtcNow));
            var revision = CatalogCare.Revision(store);
            var plan = CatalogCare.Preview(store, directory, backup);
            Assert.Equal(1, plan.Workspaces);
            Assert.Equal(2, store.All<Workspace>("Workspaces").Count);
            Assert.NotEqual(backup, plan.StagedPath);
            Assert.Equal(revision, CatalogCare.Revision(store));
            Assert.Equal(revision, plan.CatalogRevision);
            store.Put("UserSettings", "changed", null, new { Value = 1 });
            Assert.NotEqual(plan.CatalogRevision, CatalogCare.Revision(store));
            using var jobs = new JobQueue(store, null!, null!);
            Assert.Equal(
                "RECOVERY_CONTEXT_CHANGED",
                Assert.Throws<AppException>(() => CatalogCare.Validate(store, new(plan.Id, true), jobs)).Code
            );
            var fresh = CatalogCare.Preview(store, directory, backup);
            File.AppendAllText(fresh.StagedPath, "Synthetic tamper");
            Assert.Equal(
                "RECOVERY_BACKUP_CHANGED",
                Assert.Throws<AppException>(() => CatalogCare.Validate(store, new(fresh.Id, true), jobs)).Code
            );
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
