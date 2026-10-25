using System.Collections.Concurrent;
using System.Text.Json;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;

namespace Schemora.Host;

public sealed record SavedRelationDecision(
    string RelationId,
    string Decision,
    string Reason,
    DateTimeOffset ChangedAt,
    RelationshipEdge Original
);

public sealed record EfProjectMapping(string ProjectId, string DatabaseId);

public sealed record DatabaseAlias(string Name, string Reason);

public sealed record SettingValue(JsonElement Value);

public sealed record RelationshipContextSnapshot(string Revision, RelationshipGraph Graph);

public interface IRelationshipContext
{
    string ContextRevision(string workspaceId);
    Task<RelationshipContextSnapshot> GetSnapshot(string workspaceId, CancellationToken ct);
}

public sealed class RelationshipService(CatalogStore store, RelationshipAnalyzer analyzer) : IRelationshipContext
{
    private readonly ConcurrentDictionary<string, (string Key, RelationshipGraph Graph)> cache = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public void Invalidate(string workspaceId) => cache.TryRemove(workspaceId, out _);

    private sealed record RelationshipContext(
        string Revision,
        IReadOnlyList<DatabaseInfo> Databases,
        IReadOnlyList<DiscoveredSource> Sources,
        IReadOnlyList<SavedRelationDecision> Decisions,
        IReadOnlyList<EfProjectMapping> Mappings,
        IReadOnlyList<ManualRelationRecord> ManualRelations,
        IReadOnlyDictionary<string, CSharpProjectContext> ProjectContexts
    );

    // Capture every persisted graph input under the same catalog write gate. Sorting and structured
    // serialization avoid order-dependent revisions and ambiguous concatenations of identifiers.
    private RelationshipContext Capture(string workspaceId) =>
        store.WithWriteLock(() =>
        {
            WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
            var catalog = store.Catalog(workspaceId);
            var databases = catalog.Databases.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            var sources = catalog.Sources.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            var decisions = store
                .All<SavedRelationDecision>("RelationOverrides", workspaceId)
                .OrderBy(d => d.RelationId, StringComparer.Ordinal)
                .ToArray();
            var mappings = sources
                .Where(s => s.Kind == "csharpProject")
                .Select(p => store.Get<EfProjectMapping>("UserSettings", "ef-map:" + workspaceId + ":" + p.Id))
                .Where(m => m is not null)
                .Select(m => m!)
                .OrderBy(m => m.ProjectId, StringComparer.Ordinal)
                .ToArray();
            var manualRelations = ManualRelationCatalog.List(store, workspaceId);
            var projectContexts = sources
                .Where(s => s.Kind == "csharpProject")
                .ToDictionary(
                    p => p.Id,
                    p =>
                    {
                        var settings =
                            store.Get<CSharpProjectSettings>("UserSettings", CSharpProjectEndpoints.Key(workspaceId, p.Id)) ?? new(p.Id);
                        return CSharpProjectEndpoints.ReadContext(store, workspaceId, p.Path, settings);
                    },
                    StringComparer.Ordinal
                );
            var revision = Identity.Hash(
                JsonSerializer.Serialize(
                    new
                    {
                        Databases = databases.Select(d => new
                        {
                            d.Id,
                            d.CurrentSnapshotId,
                            d.Name,
                            d.Status,
                        }),
                        Sources = sources
                            .Where(s => s.Kind is "csharpProject" or "csharp" or "querySql" or "schemaSql" or "migrationSql")
                            .Select(s => new
                            {
                                s.Id,
                                s.Path,
                                s.Kind,
                                s.Hash,
                            }),
                        Decisions = decisions,
                        Mappings = mappings,
                        ManualRelations = manualRelations,
                        ProjectContexts = projectContexts
                            .OrderBy(p => p.Key)
                            .Select(p => new
                            {
                                p.Key,
                                p.Value.Fingerprint,
                                p.Value.Diagnostics,
                            }),
                    },
                    CatalogStore.Json
                )
            );
            return new RelationshipContext(revision, databases, sources, decisions, mappings, manualRelations, projectContexts);
        });

    public string ContextRevision(string workspaceId) => Capture(workspaceId).Revision;

    public async Task<RelationshipGraph> Get(string workspaceId, CancellationToken ct) => (await GetSnapshot(workspaceId, ct)).Graph;

    public async Task<RelationshipContextSnapshot> GetSnapshot(string workspaceId, CancellationToken ct)
    {
        var context = Capture(workspaceId);
        var databases = context.Databases;
        var sources = context.Sources;
        var decisions = context.Decisions;
        var mappings = context.Mappings;
        var key = context.Revision;
        if (cache.TryGetValue(workspaceId, out var cached) && cached.Key == key)
            return new(context.Revision, cached.Graph);
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(workspaceId, out cached) && cached.Key == key)
                return new(context.Revision, cached.Graph);
            var databaseModels = databases
                .Select(d =>
                    (
                        Database: d,
                        Graph: d.CurrentSnapshotId is { } snapshotId
                            ? store.Get<SnapshotRecord>("SchemaSnapshots", snapshotId)?.Graph
                            : null
                    )
                )
                .Where(x => x.Graph is not null)
                .Select(x => new RelationshipDatabase(x.Database.Id, x.Database.Name, x.Graph!))
                .ToArray();
            var sets = new List<EfSourceSet>();
            var diagnostics = new List<SchemaDiagnostic>();
            var projects = sources.Where(s => s.Kind == "csharpProject").OrderByDescending(p => p.Path.Length).ToArray();
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                var mapping = mappings.FirstOrDefault(m => m.ProjectId == project.Id);
                var databaseId = mapping?.DatabaseId ?? (databaseModels.Length == 1 ? databaseModels[0].Id : null);
                var paths = sources
                    .Where(s => s.Kind == "csharp" && Within(s.Path, Path.GetDirectoryName(project.Path)!) && !assigned.Contains(s.Id))
                    .ToArray();
                foreach (var source in paths)
                    assigned.Add(source.Id);
                if (databaseId is null || databaseModels.All(d => d.Id != databaseId))
                {
                    diagnostics.Add(
                        new(
                            "warning",
                            "EF_DATABASE_MAPPING_REQUIRED",
                            $"Select a database for C# project {project.Path}; the mapping is not applied to all databases at once."
                        )
                    );
                    continue;
                }
                sets.Add(new(databaseId, paths.Select(p => p.Path).ToArray(), context.ProjectContexts.GetValueOrDefault(project.Id)));
            }
            if (projects.Length == 0 && databaseModels.Length == 1)
            {
                var paths = sources.Where(s => s.Kind == "csharp").Select(s => s.Path).ToArray();
                if (paths.Length > 0)
                    sets.Add(new(databaseModels[0].Id, paths));
            }
            var manualTables = databaseModels.SelectMany(d => d.Graph.Tables.Select(t => new RelationshipTable(d.Id, d.Name, t))).ToArray();
            var manualEdges = context
                .ManualRelations.Select(r => ManualRelationCatalog.Edge(r, ManualRelationCatalog.IsCurrent(r, manualTables)))
                .ToArray();
            var staleManualIds = manualEdges
                .Where(edge => !edge.IsTrusted)
                .Select(edge => edge.Relation.Id)
                .ToHashSet(StringComparer.Ordinal);
            var effectiveDecisions = decisions
                .Where(decision =>
                    !staleManualIds.Contains(decision.RelationId)
                    || !decision.Original.Evidence.Any(evidence => evidence.Kind == "manual-relation")
                )
                .ToArray();
            var graph = await Task.Run(
                () =>
                    analyzer.Analyze(
                        databaseModels,
                        sets,
                        sources.Where(s => s.Kind is "querySql" or "schemaSql" or "migrationSql").Select(s => s.Path).ToArray(),
                        effectiveDecisions.Select(d => new RelationDecision(d.RelationId, d.Decision, d.Reason, d.ChangedAt)).ToArray(),
                        ct,
                        manualEdges.Where(e => e.IsTrusted).ToArray()
                    ),
                ct
            );
            var edges = graph.Relations.ToList();
            foreach (var stale in manualEdges.Where(edge => !edge.IsTrusted))
            {
                var index = edges.FindIndex(edge => edge.Relation.Id == stale.Relation.Id);
                if (index < 0)
                    edges.Add(stale);
                else if (!edges[index].IsTrusted)
                    edges[index] = stale with { Evidence = edges[index].Evidence.Concat(stale.Evidence).Distinct().ToArray() };
                // Independently valid current physical/ORM metadata (or an unrelated earlier
                // confirmation) remains authoritative; stale manual evidence cannot confer trust.
            }
            foreach (var decision in decisions.Where(d => edges.All(e => e.Relation.Id != d.RelationId)))
                edges.Add(
                    decision.Original with
                    {
                        Status = "stale",
                        IsTrusted = false,
                        ReviewReason =
                            "The current schema no longer supports the original relationship; the decision has been retained for review.",
                    }
                );
            var staleDatabaseIds = databases
                .Where(d => d.Status is "failed" or "unavailable" or "pending")
                .Select(d => d.Id)
                .ToHashSet(StringComparer.Ordinal);
            var staleTableIds = graph
                .Tables.Where(t => staleDatabaseIds.Contains(t.DatabaseId))
                .Select(t => t.Table.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (staleTableIds.Count > 0)
            {
                edges = edges
                    .Select(e =>
                        staleTableIds.Contains(e.Relation.FromTableId) || staleTableIds.Contains(e.Relation.ToTableId)
                            ? e with
                            {
                                Status = "stale",
                                IsTrusted = false,
                                ReviewReason =
                                    "The database is available only from a previous snapshot; refresh the model before building JOINs.",
                            }
                            : e
                    )
                    .ToList();
                diagnostics.Add(
                    new("warning", "SCHEMA_STALE", "Relationships from databases that failed to refresh are excluded from trusted routes.")
                );
            }
            graph = graph with { Relations = edges, Diagnostics = graph.Diagnostics.Concat(diagnostics).ToArray() };
            if (!(store.Get<StoragePolicy>("UserSettings", "storage-policy") ?? new()).LowMemory)
                cache[workspaceId] = (key, graph);
            else
                cache.TryRemove(workspaceId, out _);
            foreach (var other in cache.Keys.Where(id => id != workspaceId).Take(Math.Max(0, cache.Count - 8)))
                cache.TryRemove(other, out _);
            return new(context.Revision, graph);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RelationshipEdge> Decide(string workspaceId, string relationId, string decision, string? reason, CancellationToken ct)
    {
        if (decision is not ("confirmed" or "rejected"))
            throw new AppException("RELATION_DECISION_INVALID", "Choose confirmed or rejected.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw new AppException("RELATION_REASON_REQUIRED", "Add a reason for the decision (up to 2000 characters).");
        var snapshot = await GetSnapshot(workspaceId, ct);
        var revision = snapshot.Revision;
        var graph = snapshot.Graph;
        var edge =
            graph.Relations.FirstOrDefault(e => e.Relation.Id == relationId)
            ?? throw new AppException("RELATION_NOT_FOUND", "Relationship not found in the current graph.", 404);
        if (edge.Relation.Kind == "declared-fk")
            throw new AppException(
                "PHYSICAL_FK_IMMUTABLE",
                "A physical foreign key is defined by the schema; edit the source and refresh the model."
            );
        if (edge.Status == "stale")
            throw new AppException("RELATION_STALE", "The stale relationship needs a new table and column mapping.");
        var saved = new SavedRelationDecision(
            relationId,
            decision == "confirmed" ? "confirm" : "reject",
            reason.Trim(),
            DateTimeOffset.UtcNow,
            edge
        );
        store.WithWriteLock(() =>
        {
            if (revision != ContextRevision(workspaceId))
                throw new AppException(
                    "RELATION_CONTEXT_CHANGED",
                    "The model changed while applying the decision. Refresh the graph and try again.",
                    409
                );
            store.Put("RelationOverrides", relationId, workspaceId, saved);
            return true;
        });
        Invalidate(workspaceId);
        return (await Get(workspaceId, ct)).Relations.First(e => e.Relation.Id == relationId);
    }

    private static bool Within(string file, string directory)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar);
    }
}
