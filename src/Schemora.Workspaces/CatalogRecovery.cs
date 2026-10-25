using Microsoft.Data.Sqlite;

namespace Schemora.Workspaces;

public static class CatalogRecovery
{
    /// <summary>Call only with the application instance lock held, before opening the catalog.</summary>
    public static string Restore(string catalogPath, string backupPath)
    {
        catalogPath = Path.GetFullPath(catalogPath);
        backupPath = Path.GetFullPath(backupPath);
        if (
            string.Equals(catalogPath, backupPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(backupPath)
            || new FileInfo(backupPath).Attributes.HasFlag(FileAttributes.ReparsePoint)
        )
            throw new InvalidOperationException("Choose a separate, regular SQLite backup file.");
        using var source = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        source.Open();
        using (var check = source.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check";
            if ((string?)check.ExecuteScalar() != "ok")
                throw new InvalidOperationException("The backup is corrupted.");
        }
        using (var check = source.CreateCommand())
        {
            check.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(check.ExecuteScalar());
            if (version is < 1 or > 2)
                throw new InvalidOperationException("The backup version is incompatible.");
        }
        using (var check = source.CreateCommand())
        {
            check.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Workspaces','Recipes','SchemaSnapshots','UserSettings')";
            if (Convert.ToInt32(check.ExecuteScalar()) != 4)
                throw new InvalidOperationException("The file is not a Schemora catalog.");
        }
        var parent = Path.GetDirectoryName(catalogPath)!;
        Directory.CreateDirectory(parent);
        var pending = Path.Combine(parent, "restore-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var previous = Path.Combine(
            parent,
            "before-restore-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".sqlite"
        );
        try
        {
            using (
                var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = pending, Pooling = false }.ToString())
            )
            {
                target.Open();
                source.BackupDatabase(target);
            }
            if (File.Exists(catalogPath))
            {
                try
                {
                    new CatalogStore(catalogPath).Backup(previous);
                }
                catch (SqliteException)
                {
                    SqliteConnection.ClearAllPools();
                    File.Copy(catalogPath, previous, true);
                    if (File.Exists(catalogPath + "-wal"))
                        File.Copy(catalogPath + "-wal", previous + "-wal", true);
                    if (File.Exists(catalogPath + "-shm"))
                        File.Copy(catalogPath + "-shm", previous + "-shm", true);
                }
            }
            SqliteConnection.ClearAllPools();
            // A validated SQLite backup contains all committed WAL content. Instance lock excludes writers.
            if (File.Exists(catalogPath + "-wal"))
                File.Delete(catalogPath + "-wal");
            if (File.Exists(catalogPath + "-shm"))
                File.Delete(catalogPath + "-shm");
            File.Move(pending, catalogPath, true);
            return previous;
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }
}
