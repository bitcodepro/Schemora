using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record AutoRefreshSettings(bool Enabled = false, int DebounceSeconds = 3, int ReconcileMinutes = 5);

public sealed record AutoRefreshStatus(
    string State,
    int WatcherCount,
    bool Pending,
    DateTimeOffset? LastEventAt,
    DateTimeOffset? LastQueuedAt,
    string? LastJobId,
    IReadOnlyList<string> Diagnostics
);

public sealed record AutoRefreshView(AutoRefreshSettings Settings, AutoRefreshStatus Status);

/// <summary>Read-only scans only. Builds always require a separate explicit trust action.</summary>
public sealed class AutoRefreshService(CatalogStore store, JobQueue jobs, Toolchain toolchain) : BackgroundService
{
    private sealed class WatchState : IDisposable
    {
        public AutoRefreshSettings Settings = new();
        public string Signature = "";
        public bool Pending,
            Full;
        public DateTimeOffset? LastEventAt,
            LastQueuedAt;
        public DateTimeOffset Due,
            NextReconcile;
        public string? LastJobId;
        public List<FileSystemWatcher> Watchers = [];
        public List<string> Diagnostics = [];

        public void Dispose()
        {
            foreach (var watcher in Watchers)
                watcher.Dispose();
            Watchers.Clear();
        }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, WatchState> states = new(StringComparer.Ordinal);
    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "work",
        "artifacts",
    };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql",
        ".sqlproj",
        ".dacpac",
        ".cs",
        ".csproj",
        ".sln",
        ".slnx",
        ".props",
        ".targets",
        ".json",
        ".config",
    };

    private static string Key(string workspaceId) => "auto-refresh:" + workspaceId;

    public AutoRefreshView Get(string workspaceId)
    {
        var workspace = WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        lock (gate)
        {
            var state = Ensure(workspace, DateTimeOffset.UtcNow);
            return View(workspaceId, state);
        }
    }

    public AutoRefreshView Update(string workspaceId, AutoRefreshSettings settings)
    {
        if (settings.DebounceSeconds is < 2 or > 60 || settings.ReconcileMinutes is < 1 or > 60)
            throw new AppException("AUTO_REFRESH_SETTINGS_INVALID", "Delay: 2–60 seconds; missed-event reconciliation: 1–60 minutes.");
        var workspace = WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        store.Put("UserSettings", Key(workspaceId), workspaceId, settings);
        lock (gate)
        {
            if (states.Remove(workspaceId, out var old))
                old.Dispose();
            return View(workspaceId, Ensure(workspace, DateTimeOffset.UtcNow));
        }
    }

    private AutoRefreshView View(string id, WatchState state)
    {
        var status =
            !state.Settings.Enabled ? "disabled"
            : jobs.IsActive(id) ? "scanning"
            : state.Pending ? "pending"
            : state.Diagnostics.Count > 0 ? "degraded"
            : "watching";
        return new(
            state.Settings,
            new(
                status,
                state.Watchers.Count,
                state.Pending,
                state.LastEventAt,
                state.LastQueuedAt,
                state.LastJobId,
                state.Diagnostics.ToArray()
            )
        );
    }

    private WatchState Ensure(Workspace workspace, DateTimeOffset now)
    {
        var settings = store.Get<AutoRefreshSettings>("UserSettings", Key(workspace.Id)) ?? new();
        var signature = string.Join("\0", workspace.Roots.Concat(workspace.Excludes));
        if (states.TryGetValue(workspace.Id, out var existing) && existing.Signature == signature && existing.Settings == settings)
            return existing;
        existing?.Dispose();
        var state = new WatchState
        {
            Settings = settings,
            Signature = signature,
            NextReconcile = now.AddMinutes(settings.ReconcileMinutes),
            Full = existing?.Full ?? false,
            LastEventAt = existing?.LastEventAt,
            LastQueuedAt = existing?.LastQueuedAt,
            LastJobId = existing?.LastJobId,
        };
        states[workspace.Id] = state;
        if (!settings.Enabled)
            return state;
        state.Pending = true;
        state.Due = now.AddSeconds(settings.DebounceSeconds); // Recover changes missed while the application was closed.
        foreach (
            var root in workspace
                .Roots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Take(20)
        )
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16 * 1024,
                };
                watcher.Changed += (_, e) => Changed(workspace.Id, state, root, e.FullPath);
                watcher.Created += (_, e) => Changed(workspace.Id, state, root, e.FullPath);
                watcher.Deleted += (_, e) => Changed(workspace.Id, state, root, e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Changed(workspace.Id, state, root, e.OldFullPath);
                    Changed(workspace.Id, state, root, e.FullPath);
                };
                watcher.Error += (_, _) => MarkOverflow(workspace.Id, state);
                state.Watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                state.Diagnostics.Add("The root folder cannot be watched; changes will be checked periodically.");
            }
        }
        return state;
    }

    private void Changed(string id, WatchState state, string root, string path)
    {
        if (!Relevant(root, path))
            return;
        lock (gate)
        {
            if (!states.TryGetValue(id, out var current) || !ReferenceEquals(current, state) || !state.Settings.Enabled)
                return;
            var now = DateTimeOffset.UtcNow;
            state.Pending = true;
            state.LastEventAt = now;
            state.Due = now.AddSeconds(state.Settings.DebounceSeconds);
        }
    }

    private bool Relevant(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IgnoredSegments.Contains))
            return false;
        var dataRelative = Path.GetRelativePath(toolchain.DataDirectory, path);
        if (
            dataRelative != ".."
            && !dataRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(dataRelative)
        )
            return false;
        var extension = Path.GetExtension(path);
        return extension.Length == 0
            || Extensions.Contains(extension)
            || Path.GetFileName(path).Equals(".gitignore", StringComparison.OrdinalIgnoreCase);
    }

    private void MarkOverflow(string id, WatchState state)
    {
        lock (gate)
        {
            if (!states.TryGetValue(id, out var current) || !ReferenceEquals(current, state))
                return;
            state.Pending = true;
            state.Full = true;
            state.Signature = "";
            state.Due = DateTimeOffset.UtcNow.AddSeconds(state.Settings.DebounceSeconds);
            state.LastEventAt = DateTimeOffset.UtcNow;
            if (!state.Diagnostics.Contains("The file event stream was interrupted; a full analysis without building has been scheduled."))
                state.Diagnostics.Add("The file event stream was interrupted; a full analysis without building has been scheduled.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var workspaces = store.All<Workspace>("Workspaces");
                lock (gate)
                {
                    foreach (var id in states.Keys.Except(workspaces.Select(w => w.Id)).ToArray())
                    {
                        states[id].Dispose();
                        states.Remove(id);
                    }
                    var now = DateTimeOffset.UtcNow;
                    foreach (var workspace in workspaces)
                    {
                        var state = Ensure(workspace, now);
                        if (!state.Settings.Enabled)
                            continue;
                        if (now >= state.NextReconcile)
                        {
                            state.Pending = true;
                            state.Due = now;
                            state.NextReconcile = now.AddMinutes(state.Settings.ReconcileMinutes);
                            if (state.Watchers.Count < workspace.Roots.Distinct().Count())
                            {
                                state.Signature = "";
                                Ensure(workspace, now);
                                continue;
                            }
                        }
                        if (!state.Pending || now < state.Due || jobs.IsActive(workspace.Id))
                            continue;
                        try
                        {
                            var job = jobs.Enqueue(workspace, state.Full ? "full" : "quick", null, false);
                            state.Pending = false;
                            state.Full = false;
                            state.LastQueuedAt = now;
                            state.LastJobId = job.Id;
                            state.NextReconcile = now.AddMinutes(state.Settings.ReconcileMinutes);
                        }
                        catch (AppException ex) when (ex.Code is "WORKSPACE_BUSY" or "QUEUE_FULL")
                        {
                            state.Due = now.AddSeconds(2);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                foreach (var state in states.Values)
                    state.Dispose();
                states.Clear();
            }
        }
    }
}
