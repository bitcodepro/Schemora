using System.Security.Cryptography;
using System.Text;
using Schemora.SqlServer;
using Schemora.Workspaces.Discovery;

namespace Schemora.Workspaces;

public sealed record Workspace(
    string Id,
    string Name,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Excludes,
    DateTimeOffset CreatedAt
);

public sealed record DatabaseInfo(
    string Id,
    string WorkspaceId,
    string Name,
    string SourcePath,
    string SourceKind,
    string Status,
    string? CurrentSnapshotId,
    DateTimeOffset? LastAnalyzedAt
);

public sealed record SnapshotRecord(
    string Id,
    string WorkspaceId,
    string DatabaseId,
    DateTimeOffset CreatedAt,
    string Mode,
    string Fingerprint,
    SchemaGraph Graph,
    string? BuildManifestId = null,
    int AnalysisVersion = 1
)
{
    public SnapshotSummary Summary() => new(Id, DatabaseId, CreatedAt, Mode, Fingerprint, Graph.Tables.Count, Graph.Relations.Count);
}

public sealed record SnapshotSummary(
    string Id,
    string DatabaseId,
    DateTimeOffset CreatedAt,
    string Mode,
    string Fingerprint,
    int TableCount,
    int RelationCount
);

public sealed record ScanRun(
    string Id,
    string WorkspaceId,
    string Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Status,
    int ChangedSources,
    int UnchangedSources,
    int DatabasesUpdated,
    IReadOnlyList<string> Diagnostics
);

public sealed record WorkspaceCatalog(
    Workspace Workspace,
    IReadOnlyList<DiscoveredSource> Sources,
    IReadOnlyList<DatabaseInfo> Databases,
    IReadOnlyList<ScanRun> ScanRuns
);

public sealed record ScanOutcome(
    int ChangedSources,
    int UnchangedSources,
    int DatabasesUpdated,
    IReadOnlyList<string> Diagnostics,
    bool Partial,
    int RecipesChecked = 0
);

public sealed record StoredRecord(string Table, string Id, string? WorkspaceId, object Value);

public static class Identity
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string PathKey(string path) =>
        OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);

    public static SchemaGraph Scope(SchemaGraph graph, string databaseId)
    {
        var ids = graph
            .Tables.Select(t => (t.Id, t.Schema, t.Name))
            .Concat((graph.Objects ?? []).Select(o => (o.Id, o.Schema, o.Name)))
            .ToDictionary(t => t.Id, t => databaseId + ":" + Hash(t.Schema + "\0" + t.Name), StringComparer.Ordinal);
        return graph with
        {
            Tables = graph
                .Tables.Select(t =>
                    t with
                    {
                        Id = ids[t.Id],
                        UniqueKeys = (t.UniqueKeys ?? []).Select(k => k with { Id = databaseId + ":" + k.Id }).ToArray(),
                    }
                )
                .ToArray(),
            Objects = (graph.Objects ?? []).Select(o => o with { Id = ids[o.Id] }).ToArray(),
            Indexes = (graph.Indexes ?? [])
                .Where(i => ids.ContainsKey(i.ObjectId))
                .Select(i => i with { Id = databaseId + ":" + i.Id, ObjectId = ids[i.ObjectId] })
                .ToArray(),
            ObjectDependencies = (graph.ObjectDependencies ?? [])
                .Where(d => ids.ContainsKey(d.FromObjectId))
                .Select(d =>
                    d with
                    {
                        Id = databaseId + ":" + d.Id,
                        FromObjectId = ids[d.FromObjectId],
                        ToObjectId = d.ToObjectId is { } target && ids.TryGetValue(target, out var scoped) ? scoped : null,
                    }
                )
                .ToArray(),
            Relations = graph
                .Relations.Where(r => ids.ContainsKey(r.FromTableId) && ids.ContainsKey(r.ToTableId))
                .Select(r => r with { Id = databaseId + ":" + r.Id, FromTableId = ids[r.FromTableId], ToTableId = ids[r.ToTableId] })
                .ToArray(),
        };
    }
}
