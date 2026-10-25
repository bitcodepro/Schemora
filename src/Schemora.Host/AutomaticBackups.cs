using System.Globalization;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record BackupSchedule(bool Enabled = true, int IntervalHours = 24, int Keep = 7);

public sealed record AutomaticBackup(string Name, string Path, long Bytes, DateTimeOffset CreatedAt);

public sealed record BackupScheduleStatus(
    BackupSchedule Schedule,
    IReadOnlyList<AutomaticBackup> Backups,
    DateTimeOffset? NextAt,
    string? LastError,
    DateTimeOffset? LastErrorAt
);

/// <summary>
/// Catalog copies taken on a schedule into backups/auto-*.sqlite, keeping the newest N. Only files this class names are ever
/// pruned; manual and pre-restore backups are left alone. The catalog never holds runtime values, so neither do the copies.
/// </summary>
public sealed class AutomaticBackups(CatalogStore store) : BackgroundService
{
    private const string SettingKey = "backup-schedule";
    private const string Prefix = "auto-";
    private readonly Lock gate = new();
    private string? lastError;
    private DateTimeOffset? lastErrorAt;

    private string Folder => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(store.Path))!, "backups");

    public BackupSchedule Schedule() => store.Get<BackupSchedule>("UserSettings", SettingKey) ?? new();

    public BackupScheduleStatus Save(BackupSchedule schedule)
    {
        if (schedule.IntervalHours is < 1 or > 24 * 30 || schedule.Keep is < 1 or > 100)
            throw new AppException("BACKUP_SCHEDULE_INVALID", "Back up every 1–720 hours and keep 1–100 copies.");
        store.Put("UserSettings", SettingKey, null, schedule);
        lock (gate)
            Prune(schedule.Keep);
        return Status();
    }

    public BackupScheduleStatus Status()
    {
        var schedule = Schedule();
        var backups = List();
        return new(
            schedule,
            backups,
            schedule.Enabled ? (backups.FirstOrDefault()?.CreatedAt.AddHours(schedule.IntervalHours) ?? DateTimeOffset.UtcNow) : null,
            lastError,
            lastErrorAt
        );
    }

    /// <summary>Takes a copy now, whatever the schedule says, then applies the retention.</summary>
    public AutomaticBackup BackUpNow()
    {
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            LocalFiles.CheckNoLinks(Folder);
            var now = DateTimeOffset.UtcNow;
            var path = System.IO.Path.Combine(
                Folder,
                Prefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".sqlite"
            );
            store.Backup(path);
            Prune(Schedule().Keep);
            lastError = null;
            lastErrorAt = null;
            return new(System.IO.Path.GetFileName(path), path, new FileInfo(path).Length, now);
        }
    }

    public static bool IsAutomatic(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal)
        && fileName.EndsWith(".sqlite", StringComparison.Ordinal)
        && fileName.Length == Prefix.Length + 15 + 1 + 32 + 7
        && Guid.TryParseExact(fileName[^39..^7], "N", out _);

    public IReadOnlyList<AutomaticBackup> List() =>
        !Directory.Exists(Folder)
            ? []
            : Directory
                .EnumerateFiles(Folder, Prefix + "*.sqlite", SearchOption.TopDirectoryOnly)
                .Where(path => IsAutomatic(System.IO.Path.GetFileName(path)))
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                .Select(path => new AutomaticBackup(
                    System.IO.Path.GetFileName(path),
                    path,
                    new FileInfo(path).Length,
                    new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                ))
                .OrderByDescending(backup => backup.CreatedAt)
                .ThenByDescending(backup => backup.Name, StringComparer.Ordinal)
                .ToArray();

    private void Prune(int keep)
    {
        foreach (var old in List().Skip(keep))
            File.Delete(old.Path);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first check waits a little so start-up, migrations and the first scan are not slowed by a copy.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            do
            {
                try
                {
                    var schedule = Schedule();
                    var latest = List().FirstOrDefault();
                    if (schedule.Enabled && (latest is null || latest.CreatedAt.AddHours(schedule.IntervalHours) <= DateTimeOffset.UtcNow))
                        BackUpNow();
                }
                catch (Exception failure)
                    when (failure is IOException or UnauthorizedAccessException or AppException or Microsoft.Data.Sqlite.SqliteException)
                {
                    lastError = failure.Message;
                    lastErrorAt = DateTimeOffset.UtcNow;
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public static class AutomaticBackupEndpoints
{
    public static void MapAutomaticBackups(this WebApplication app)
    {
        app.MapGet("/api/storage/backup-schedule", (AutomaticBackups backups) => backups.Status());
        app.MapPut("/api/storage/backup-schedule", (BackupSchedule request, AutomaticBackups backups) => backups.Save(request));
        app.MapPost(
            "/api/storage/backup-schedule/run",
            (AutomaticBackups backups) =>
            {
                backups.BackUpNow();
                return backups.Status();
            }
        );
    }
}
