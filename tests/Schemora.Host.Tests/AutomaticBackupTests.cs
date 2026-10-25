using Schemora.Host;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class AutomaticBackupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraBackups-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogStore store;

    public AutomaticBackupTests()
    {
        Directory.CreateDirectory(directory);
        store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
        store.Put("Workspaces", "w", "w", new Workspace("w", "Workspace", [], [], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CopiesKeepTheNewestAndLeaveOtherBackupsAlone()
    {
        var backups = new AutomaticBackups(store);
        var manual = Path.Combine(directory, "backups", "schemora-20260101-000000-" + Guid.NewGuid().ToString("N") + ".sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(manual)!);
        File.WriteAllText(manual, "manual");
        backups.Save(new BackupSchedule(true, 24, 2));
        var first = backups.BackUpNow();
        Assert.True(AutomaticBackups.IsAutomatic(first.Name));
        Assert.Single(new CatalogStore(first.Path).All<Workspace>("Workspaces"));
        // Reading the copy above left a pooled connection on it; the app itself never opens its own copies.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.SetLastWriteTimeUtc(first.Path, DateTime.UtcNow.AddHours(-3));
        var second = backups.BackUpNow();
        File.SetLastWriteTimeUtc(second.Path, DateTime.UtcNow.AddHours(-2));
        var third = backups.BackUpNow();
        Assert.Equal([third.Name, second.Name], backups.List().Select(backup => backup.Name));
        Assert.False(File.Exists(first.Path));
        Assert.True(File.Exists(manual));
        // Lowering the retention prunes at once.
        Assert.Equal([third.Name], backups.Save(new BackupSchedule(true, 24, 1)).Backups.Select(backup => backup.Name));
        Assert.True(File.Exists(manual));
    }

    [Fact]
    public void TheScheduleIsValidatedAndPlansTheNextCopy()
    {
        var backups = new AutomaticBackups(store);
        Assert.Equal(new BackupSchedule(), backups.Schedule());
        Assert.Equal("BACKUP_SCHEDULE_INVALID", Assert.Throws<AppException>(() => backups.Save(new BackupSchedule(true, 0, 3))).Code);
        Assert.Equal("BACKUP_SCHEDULE_INVALID", Assert.Throws<AppException>(() => backups.Save(new BackupSchedule(true, 24, 101))).Code);
        Assert.Null(backups.Save(new BackupSchedule(false, 12, 3)).NextAt);
        var latest = backups.BackUpNow();
        var status = backups.Save(new BackupSchedule(true, 12, 3));
        Assert.Equal(latest.CreatedAt.AddHours(12), status.NextAt!.Value, TimeSpan.FromSeconds(2));
        Assert.False(AutomaticBackups.IsAutomatic("auto-not-a-backup.sqlite"));
        Assert.False(AutomaticBackups.IsAutomatic("before-restore-20260101-000000-" + Guid.NewGuid().ToString("N") + ".sqlite"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
