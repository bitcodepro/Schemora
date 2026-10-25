using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record SearchHit(
    string Kind,
    string Id,
    string Name,
    string? DatabaseId,
    string? SourcePath,
    int? Line,
    string Detail,
    int? DefinitionLine = null
);

/// <summary>
/// Workspace search over an in-memory index built once per catalog state (current snapshots, recipes and relation
/// overrides) instead of expanding every snapshot's JSON on each keystroke. Results are ranked: exact names first,
/// then name prefixes, then other name matches, then matches in definitions and details.
/// </summary>
public static class WorkspaceSearch
{
    private sealed record Entry(string Kind, string Id, string Name, string? DatabaseId, string? Path, int? Line, string Detail)
    {
        public string LowerName { get; } = Name.ToLowerInvariant();
        public string LowerDetail { get; } = Detail.ToLowerInvariant();
    }

    private sealed record Index(string Fingerprint, Entry[] Entries)
    {
        public long LastUsed { get; set; }
    }

    private const int MaximumCachedWorkspaces = 8;
    private static readonly ConcurrentDictionary<string, Index> Cache = new(StringComparer.Ordinal);

    private static readonly string[] KindOrder = ["table", "view", "column", "recipe", "procedure", "function", "sqlCode", "evidence"];

    public static object Read(
        CatalogStore store,
        string workspaceId,
        string query,
        string? kind,
        int offset,
        int limit,
        string? databaseId = null
    )
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        if (query.Length > 256)
            throw new AppException("SEARCH_QUERY_INVALID", "Search is limited to 256 characters.");
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        var needle = query.Trim().ToLowerInvariant();
        var matches = new List<(Entry Entry, int Rank)>();
        foreach (var entry in Load(store, workspaceId).Entries)
        {
            if (!string.IsNullOrEmpty(databaseId) && entry.DatabaseId != databaseId && entry.Kind != "recipe")
                continue;
            var rank = Rank(entry, needle);
            if (rank >= 0)
                matches.Add((entry, rank));
        }
        var kinds = matches
            .GroupBy(match => match.Entry.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var filtered = matches.Where(match => string.IsNullOrEmpty(kind) || match.Entry.Kind == kind).ToList();
        var items = filtered
            .OrderBy(match => match.Rank)
            .ThenBy(match => KindPosition(match.Entry.Kind))
            .ThenBy(match => match.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Entry.Id, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .Select(match => Hit(match.Entry, query.Trim()))
            .ToArray();
        return new
        {
            Items = items,
            Total = filtered.Count,
            Offset = offset,
            Limit = limit,
            Kinds = kinds,
        };
    }

    /// <summary>0 exact name or last name part, 1 prefix, 2 name contains, 3 detail only, -1 no match.</summary>
    private static int Rank(Entry entry, string needle)
    {
        if (entry.Kind == "sqlCode")
            return needle.Length > 0 && entry.LowerDetail.Contains(needle, StringComparison.Ordinal) ? 3 : -1;
        if (needle.Length == 0)
            return 2;
        var name = entry.LowerName;
        var last = name[(name.LastIndexOf('.') + 1)..];
        if (name == needle || last == needle)
            return 0;
        if (name.StartsWith(needle, StringComparison.Ordinal) || last.StartsWith(needle, StringComparison.Ordinal))
            return 1;
        if (name.Contains(needle, StringComparison.Ordinal))
            return 2;
        return entry.LowerDetail.Contains(needle, StringComparison.Ordinal) ? 3 : -1;
    }

    private static int KindPosition(string kind)
    {
        var index = Array.IndexOf(KindOrder, kind);
        return index < 0 ? KindOrder.Length : index;
    }

    private static SearchHit Hit(Entry entry, string query)
    {
        if (entry.Kind != "sqlCode")
            return new(entry.Kind, entry.Id, entry.Name, entry.DatabaseId, entry.Path, entry.Line, entry.Detail);
        var at = Math.Max(0, entry.Detail.IndexOf(query, StringComparison.OrdinalIgnoreCase));
        var definitionLine = 1 + entry.Detail[..at].Count(c => c == '\n');
        var start = Math.Max(0, at - 80);
        return new(
            entry.Kind,
            entry.Id,
            entry.Name,
            entry.DatabaseId,
            entry.Path,
            entry.Line is { } line ? line + definitionLine - 1 : null,
            entry.Detail.Substring(start, Math.Min(300, entry.Detail.Length - start)),
            definitionLine
        );
    }

    private static Index Load(CatalogStore store, string workspaceId)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = store.Path, Mode = SqliteOpenMode.ReadOnly }.ToString()
        );
        connection.Open();
        using var transaction = connection.BeginTransaction();
        SqliteCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$ws", workspaceId);
            return command;
        }
        // Everything the index is made of changes one of these values: a new snapshot, a saved or archived recipe, a reviewed relation.
        using var stamp = Command(
            """
            SELECT coalesce((SELECT group_concat(Id||':'||coalesce(json_extract(Json,'$.currentSnapshotId'),''),',') FROM Databases WHERE WorkspaceId=$ws),'')
              ||'|'||(SELECT count(*)||':'||coalesce(max(UpdatedAt),'') FROM Recipes WHERE WorkspaceId=$ws)
              ||'|'||(SELECT count(*)||':'||coalesce(max(UpdatedAt),'') FROM RelationOverrides WHERE WorkspaceId=$ws)
            """
        );
        var fingerprint = Convert.ToString(stamp.ExecuteScalar()) ?? "";
        if (Cache.TryGetValue(workspaceId, out var cached) && cached.Fingerprint == fingerprint)
        {
            cached.LastUsed = Environment.TickCount64;
            return cached;
        }
        using var command = Command(
            """
            WITH models AS(SELECT d.Id db,s.Json snapshot FROM Databases d JOIN SchemaSnapshots s ON s.Id=json_extract(d.Json,'$.currentSnapshotId') WHERE d.WorkspaceId=$ws),
            objects AS(SELECT db,json_extract(snapshot,'$.graph.sourcePath') path,o.value obj,'table' kind FROM models,json_each(snapshot,'$.graph.tables') o UNION ALL SELECT db,json_extract(snapshot,'$.graph.sourcePath'),o.value,json_extract(o.value,'$.kind') FROM models,json_each(snapshot,'$.graph.objects') o)
            SELECT kind,json_extract(obj,'$.id'),json_extract(obj,'$.schema')||'.'||json_extract(obj,'$.name'),db,coalesce(json_extract(obj,'$.sourcePath'),path),json_extract(obj,'$.line'),'' FROM objects
            UNION ALL SELECT 'sqlCode',json_extract(obj,'$.id'),json_extract(obj,'$.schema')||'.'||json_extract(obj,'$.name'),db,coalesce(json_extract(obj,'$.sourcePath'),objects.path),json_extract(obj,'$.line'),json_extract(obj,'$.definition') FROM objects WHERE length(json_extract(obj,'$.definition'))>0
            UNION ALL SELECT 'column',json_extract(obj,'$.id'),json_extract(obj,'$.schema')||'.'||json_extract(obj,'$.name')||'.'||json_extract(c.value,'$.name'),db,objects.path,NULL,json_extract(c.value,'$.dataType') FROM objects,json_each(obj,'$.columns') c
            UNION ALL SELECT 'recipe',Id,json_extract(Json,'$.definition.name'),NULL,NULL,NULL,json_extract(Json,'$.definition.description') FROM Recipes WHERE WorkspaceId=$ws AND coalesce(json_extract(Json,'$.archived'),0)=0
            UNION ALL SELECT 'evidence',json_extract(d.value,'$.fromObjectId'),json_extract(d.value,'$.targetName'),db,json_extract(d.value,'$.sourcePath'),json_extract(d.value,'$.line'),json_extract(d.value,'$.kind') FROM models,json_each(snapshot,'$.graph.objectDependencies') d
            UNION ALL SELECT 'evidence',json_extract(r.Json,'$.original.relation.fromTableId'),json_extract(r.Json,'$.original.relation.name'),NULL,json_extract(e.value,'$.sourcePath'),json_extract(e.value,'$.line'),json_extract(e.value,'$.detail') FROM RelationOverrides r,json_each(r.Json,'$.original.evidence') e WHERE r.WorkspaceId=$ws
            """
        );
        var entries = new List<Entry>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                string? S(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                entries.Add(
                    new(S(0) ?? "", S(1) ?? "", S(2) ?? "", S(3), S(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), S(6) ?? "")
                );
            }
        var index = new Index(fingerprint, entries.ToArray()) { LastUsed = Environment.TickCount64 };
        Cache[workspaceId] = index;
        // Only the least recently searched workspaces are dropped, so a ninth workspace does not re-index the others.
        foreach (
            var stale in Cache
                .OrderByDescending(pair => pair.Value.LastUsed)
                .Skip(MaximumCachedWorkspaces)
                .Select(pair => pair.Key)
                .ToArray()
        )
            Cache.TryRemove(stale, out _);
        return index;
    }
}
