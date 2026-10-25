using System.Text.Json;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;

namespace Schemora.Host;

public sealed record AnalysisProjectReceipt(string ProjectId, string Configuration, string? Framework, bool TrustedEvaluation);

public sealed record AnalysisReceipt(
    string Id,
    string WorkspaceId,
    DateTimeOffset CreatedAt,
    string Mode,
    string ApplicationVersion,
    string RuntimeVersion,
    string DacFxVersion,
    int AnalysisVersion,
    IReadOnlyList<DiscoveredSource> Sources,
    IReadOnlyList<AnalysisProjectReceipt> Projects,
    IReadOnlyDictionary<string, string?> Snapshots,
    IReadOnlyList<string> Excludes,
    IReadOnlyList<string> Roots,
    string SettingsHash = "",
    IReadOnlyList<AnalysisBuildReceipt>? Builds = null
);

public static class AnalysisProvenance
{
    public static string SettingsHash(CatalogStore store, string workspaceId) =>
        Identity.Hash(
            JsonSerializer.Serialize(
                new
                {
                    Overrides = store
                        .All<JsonElement>("RelationOverrides", workspaceId)
                        .Select(e => e.GetRawText())
                        .Order(StringComparer.Ordinal),
                    Settings = store
                        .Inventory("UserSettings", workspaceId)
                        .Where(r => r.Id.StartsWith("ef-map:") || r.Id.StartsWith("csharp-context:") || r.Id.StartsWith("db-name:"))
                        .OrderBy(r => r.Id, StringComparer.Ordinal)
                        .Select(r => new { r.Id, Value = store.Get<JsonElement>("UserSettings", r.Id) }),
                },
                CatalogStore.Json
            )
        );

    public static AnalysisReceipt Capture(
        CatalogStore store,
        Workspace workspace,
        ScanRun run,
        IReadOnlyList<DiscoveredSource> sources,
        IReadOnlyList<DatabaseInfo> databases,
        IReadOnlyList<SnapshotRecord>? pending = null
    ) =>
        new(
            run.Id,
            workspace.Id,
            run.FinishedAt,
            run.Mode,
            typeof(AnalysisProvenance).Assembly.GetName().Version?.ToString() ?? "unknown",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            typeof(Microsoft.SqlServer.Dac.Model.TSqlModel).Assembly.GetName().Version?.ToString() ?? "unknown",
            WorkspaceScanner.CurrentAnalysisVersion,
            sources,
            sources
                .Where(s => s.Kind == "csharpProject")
                .Select(p =>
                {
                    var setting =
                        store.Get<CSharpProjectSettings>("UserSettings", CSharpProjectEndpoints.Key(workspace.Id, p.Id)) ?? new(p.Id);
                    return new AnalysisProjectReceipt(p.Id, setting.Configuration, setting.TargetFramework, setting.TrustedEvaluation);
                })
                .ToArray(),
            databases.ToDictionary(d => d.Id, d => d.CurrentSnapshotId),
            workspace.Excludes,
            workspace.Roots,
            SettingsHash(store, workspace.Id),
            BuildToolCapture.FromSnapshots(store, databases, pending)
        );

    public static async Task<object> Report(CatalogStore store, string workspaceId, WorkspaceDiscovery discovery, CancellationToken ct)
    {
        var catalog = store.Catalog(workspaceId);
        var receipts = store.Recent<AnalysisReceipt>("AnalysisReceipts", 20, workspaceId).ToArray();
        var latest = receipts.FirstOrDefault();
        var current = await discovery.Scan(catalog.Workspace.Roots, catalog.Workspace.Excludes, null, ct);
        var prior = (latest?.Sources ?? catalog.Sources).ToDictionary(s => s.Path, s => s.Hash, StringComparer.OrdinalIgnoreCase);
        var now = current.Sources.ToDictionary(s => s.Path, s => s.Hash, StringComparer.OrdinalIgnoreCase);
        var changes = prior
            .Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(p => prior.GetValueOrDefault(p) != now.GetValueOrDefault(p))
            .Select(p => new
            {
                Path = p,
                Change = !prior.ContainsKey(p) ? "added"
                : !now.ContainsKey(p) ? "removed"
                : "modified",
                Before = prior.GetValueOrDefault(p),
                After = now.GetValueOrDefault(p),
            })
            .ToArray();
        var latestSources = latest?.Sources.ToDictionary(s => s.Path, s => s.Hash, StringComparer.OrdinalIgnoreCase);
        var previous = receipts.Skip(1).FirstOrDefault();
        var previousSources = previous?.Sources.ToDictionary(s => s.Path, s => s.Hash, StringComparer.OrdinalIgnoreCase);
        var recordedChanges =
            latest is null || previousSources is null
                ? []
                : latest
                    .Sources.Select(s => s.Path)
                    .Union(previousSources.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(p => previousSources.GetValueOrDefault(p) != latestSources!.GetValueOrDefault(p))
                    .ToArray();
        var settingsChanged =
            latest is not null
            && (
                latest.SettingsHash != SettingsHash(store, workspaceId)
                || latest.Snapshots.Count != catalog.Databases.Count
                || catalog.Databases.Any(d => latest.Snapshots.GetValueOrDefault(d.Id) != d.CurrentSnapshotId)
                || !latest.Roots.SequenceEqual(catalog.Workspace.Roots)
                || latest.Excludes.SequenceEqual(catalog.Workspace.Excludes) == false
                || latest.Projects.Any(p =>
                {
                    var settings =
                        store.Get<CSharpProjectSettings>("UserSettings", CSharpProjectEndpoints.Key(workspaceId, p.ProjectId))
                        ?? new(p.ProjectId);
                    return settings.Configuration != p.Configuration
                        || settings.TargetFramework != p.Framework
                        || settings.TrustedEvaluation != p.TrustedEvaluation;
                })
            );
        return new
        {
            Latest = latest,
            Previous = previous,
            Receipts = receipts.Select(r => new
            {
                r.Id,
                r.CreatedAt,
                r.Mode,
                r.ApplicationVersion,
                r.AnalysisVersion,
            }),
            CurrentCheckComplete = current.Complete,
            CurrentDiagnostics = current.Diagnostics,
            Stale = catalog.Databases.Any(d => d.Status is "failed" or "unavailable" or "pending")
                || latest is null
                || latest.AnalysisVersion != WorkspaceScanner.CurrentAnalysisVersion
                || !current.Complete
                || changes.Length > 0
                || settingsChanged,
            SettingsChanged = settingsChanged,
            Changes = changes,
            RecordedChangedPaths = recordedChanges,
        };
    }
}
