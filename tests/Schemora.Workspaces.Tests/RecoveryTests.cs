using Schemora.Workspaces;
using Xunit;

namespace Schemora.Workspaces.Tests;

public sealed class RecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Schemora-recovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LockPreventsTwoWritersAndRecoversAfterCrashHandleCloses()
    {
        using (var first = LocalInstance.TryAcquire(directory))
        {
            Assert.NotNull(first);
            Assert.Null(LocalInstance.TryAcquire(directory));
            first!.Publish("http://127.0.0.1:1234", new string('A', 64));
            Assert.NotNull(LocalInstance.ReadRunning(directory));
        }
        Assert.Null(LocalInstance.ReadRunning(directory));
        using var next = LocalInstance.TryAcquire(directory);
        Assert.NotNull(next);
    }

    [Fact]
    public void ValidatedBackupRestoresAndKeepsPreviousCatalog()
    {
        var path = Path.Combine(directory, "catalog.sqlite");
        var store = new CatalogStore(path);
        store.Put("UserSettings", "x", null, new { Value = "before" });
        var backup = Path.Combine(directory, "backup.sqlite");
        store.Backup(backup);
        store.Put("UserSettings", "x", null, new { Value = "after" });
        var previous = CatalogRecovery.Restore(path, backup);
        Assert.True(File.Exists(previous));
        Assert.Contains("before", new CatalogStore(path).Get<object>("UserSettings", "x")!.ToString()!);
        Assert.Contains("after", new CatalogStore(previous).Get<object>("UserSettings", "x")!.ToString()!);
    }

    [Fact]
    public void MalformedBackupCannotDamageCurrentCatalog()
    {
        var path = Path.Combine(directory, "catalog.sqlite");
        var store = new CatalogStore(path);
        store.Put("UserSettings", "x", null, new { Value = "kept" });
        var backup = Path.Combine(directory, "bad.sqlite");
        File.WriteAllText(backup, "not sqlite");
        Assert.ThrowsAny<Exception>(() => CatalogRecovery.Restore(path, backup));
        Assert.Contains("kept", store.Get<object>("UserSettings", "x")!.ToString()!);
    }

    [Fact]
    public void MigratesV1WithValidatedBackup()
    {
        var path = Path.Combine(directory, "catalog.sqlite");
        var store = new CatalogStore(path);
        store.Put("UserSettings", "x", null, new { Value = "migrated" });
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=1";
            command.ExecuteNonQuery();
        }
        var reopened = new CatalogStore(path);
        Assert.Single(Directory.GetFiles(directory, "catalog.sqlite.before-v2-*.sqlite"));
        Assert.Contains("migrated", reopened.Get<object>("UserSettings", "x")!.ToString()!);
    }

    [Fact]
    public void CanRestoreEvenWhenCurrentCatalogIsCorrupt()
    {
        var path = Path.Combine(directory, "catalog.sqlite");
        var store = new CatalogStore(path);
        store.Put("UserSettings", "x", null, new { Value = "safe" });
        var backup = Path.Combine(directory, "good.sqlite");
        store.Backup(backup);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(path, "corrupt");
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
        var prior = CatalogRecovery.Restore(path, backup);
        Assert.Equal("corrupt", File.ReadAllText(prior));
        Assert.Contains("safe", new CatalogStore(path).Get<object>("UserSettings", "x")!.ToString()!);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
