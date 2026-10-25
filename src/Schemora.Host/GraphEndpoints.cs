using System.Text.Json;
using System.Text.RegularExpressions;
using Schemora.Relationships;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;

namespace Schemora.Host;

public static class GraphEndpoints
{
    public static void MapGraphEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/workspaces/{id}/graph",
            async (
                string id,
                string? databaseId,
                string? schema,
                string? kind,
                string? query,
                string? focusTableId,
                int? hops,
                double? minimumConfidence,
                int? limit,
                GraphReadIndex index,
                CancellationToken ct
            ) =>
            {
                await index.Ensure(id, ct);
                var page = index.Graph(id, databaseId, schema, query, focusTableId, hops ?? 1, minimumConfidence ?? 0, kind, limit ?? 150);
                return new
                {
                    Tables = page.Tables.Select(Table),
                    Relations = page.Relations.Select(Edge),
                    page.Diagnostics,
                    page.TotalTables,
                    page.Truncated,
                    page.NeighborhoodTruncated,
                };
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/tables",
            async (
                string id,
                string? databaseId,
                string? schema,
                string? query,
                int? offset,
                int? limit,
                GraphReadIndex index,
                CancellationToken ct
            ) =>
            {
                await index.Ensure(id, ct);
                return index.Tables(id, databaseId, schema, query, offset ?? 0, limit ?? 100);
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/relations/page",
            async (string id, int? offset, int? limit, string? query, GraphReadIndex index, CancellationToken ct) =>
            {
                await index.Ensure(id, ct);
                var page = index.Relations(id, offset ?? 0, limit ?? 100, query);
                return new
                {
                    Items = page.Items.Select(Edge),
                    page.Total,
                    page.Offset,
                    page.Limit,
                };
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/relations",
            async (string id, int? offset, int? limit, string? query, GraphReadIndex index, CancellationToken ct) =>
            {
                await index.Ensure(id, ct);
                return index.Relations(id, offset ?? 0, limit ?? 200, query).Items.Select(Edge);
            }
        );
        app.MapPost(
            "/api/workspaces/{id}/relations/{relationId}/decision",
            async (string id, string relationId, RelationDecisionRequest request, RelationshipService service, CancellationToken ct) =>
                Edge(await service.Decide(id, relationId, request.Decision, request.Reason, ct))
        );
        app.MapPost(
            "/api/workspaces/{id}/paths",
            async (string id, PathRequest request, RelationshipService service, CancellationToken ct) =>
            {
                var graph = await service.Get(id, ct);
                if (!graph.Tables.Any(t => t.Table.Id == request.FromTableId) || !graph.Tables.Any(t => t.Table.Id == request.ToTableId))
                    throw new AppException("TABLE_NOT_FOUND", "Выберите таблицы текущей модели.", 404);
                return new RelationshipPathFinder()
                    .Find(graph, request.FromTableId, request.ToTableId)
                    .Select(p => new
                    {
                        p.TableIds,
                        p.RelationIds,
                        Sql = p.JoinSql,
                        Warnings = Array.Empty<string>(),
                        p.Explanations,
                    });
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/ef-mappings",
            (string id, CatalogStore store) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                var databases = store.All<DatabaseInfo>("Databases", id);
                return new
                {
                    Projects = store
                        .All<DiscoveredSource>("Sources", id)
                        .Where(p => p.Kind == "csharpProject")
                        .Select(p => new
                        {
                            ProjectId = p.Id,
                            p.Path,
                            DatabaseId = store.Get<EfProjectMapping>("UserSettings", "ef-map:" + id + ":" + p.Id)?.DatabaseId
                                ?? (databases.Count == 1 ? databases[0].Id : null),
                        }),
                    Databases = databases,
                };
            }
        );
        app.MapPut(
            "/api/workspaces/{id}/ef-mappings",
            (string id, EfProjectMapping request, CatalogStore store, RelationshipService service) =>
            {
                WorkspaceEndpoints.RequireWorkspace(store, id);
                if (
                    !store.All<DiscoveredSource>("Sources", id).Any(p => p.Id == request.ProjectId && p.Kind == "csharpProject")
                    || !store.All<DatabaseInfo>("Databases", id).Any(d => d.Id == request.DatabaseId)
                )
                    throw new AppException("EF_MAPPING_INVALID", "Выберите обнаруженный C#-проект и БД этой рабочей области.");
                store.Put("UserSettings", "ef-map:" + id + ":" + request.ProjectId, id, request);
                service.Invalidate(id);
                return request;
            }
        );
        app.MapPut(
            "/api/databases/{id}/name",
            (string id, DatabaseAlias request, CatalogStore store, RelationshipService service) =>
            {
                var database =
                    store.Get<DatabaseInfo>("Databases", id) ?? throw new AppException("DATABASE_NOT_FOUND", "БД не найдена.", 404);
                if (
                    string.IsNullOrWhiteSpace(request.Name)
                    || request.Name.Length > 128
                    || request.Name.Any(char.IsControl)
                    || string.IsNullOrWhiteSpace(request.Reason)
                )
                    throw new AppException("DATABASE_NAME_INVALID", "Нужны SQL-имя БД (до128 символов) и основание сопоставления.");
                var alias = request with { Name = request.Name.Trim() };
                store.Batch([
                    new("UserSettings", "db-name:" + id, database.WorkspaceId, alias),
                    new("Databases", id, database.WorkspaceId, database with { Name = alias.Name }),
                ]);
                service.Invalidate(database.WorkspaceId);
                return alias;
            }
        );
        app.MapGet(
            "/api/settings/{key}",
            (string key, CatalogStore store) =>
            {
                ValidateSettingKey(key);
                return new { Value = store.Get<SettingValue>("UserSettings", key)?.Value };
            }
        );
        app.MapPut(
            "/api/settings/{key}",
            (string key, SettingValue request, CatalogStore store) =>
            {
                ValidateSettingKey(key);
                if (request.Value.GetRawText().Length > 256 * 1024)
                    throw new AppException("SETTING_TOO_LARGE", "Настройка превышает 256 КиБ.");
                if (
                    key == "language"
                    && (request.Value.ValueKind != JsonValueKind.String || request.Value.GetString() is not ("en" or "ru" or "uk"))
                )
                    throw new AppException("LANGUAGE_INVALID", "Choose en, ru or uk for the interface language.");
                // Per-workspace settings name their workspace after the colon, which also scopes them to it.
                store.Put("UserSettings", key, key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : null, request);
                return request;
            }
        );
    }

    private static void ValidateSettingKey(string key)
    {
        if (
            key != "theme"
            && key != "language"
            && !Regex.IsMatch(key, "^(graph|recipe-picks):[a-f0-9]{32}$", RegexOptions.CultureInvariant)
        )
            throw new AppException("SETTING_KEY_INVALID", "Неизвестная настройка интерфейса.");
    }

    private static IEnumerable<RelationshipTable> Filter(
        IEnumerable<RelationshipTable> tables,
        string? databaseId,
        string? schema,
        string? query
    ) =>
        tables.Where(t =>
            (databaseId is null || t.DatabaseId == databaseId)
            && (string.IsNullOrEmpty(schema) || t.Table.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase))
            && (
                string.IsNullOrWhiteSpace(query)
                || (t.DatabaseName + "." + t.Table.Schema + "." + t.Table.Name).Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Table.Columns.Any(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            )
        );

    public static object Table(RelationshipTable wrapper) =>
        new
        {
            wrapper.DatabaseId,
            wrapper.DatabaseName,
            wrapper.Table.Id,
            wrapper.Table.Schema,
            wrapper.Table.Name,
            wrapper.Table.Columns,
            wrapper.Table.PrimaryKey,
            wrapper.Table.UniqueKeys,
        };

    public static object Edge(RelationshipEdge edge) =>
        new
        {
            edge.Relation.Id,
            edge.Relation.Name,
            edge.Relation.FromTableId,
            edge.Relation.ToTableId,
            edge.Relation.FromColumns,
            edge.Relation.ToColumns,
            edge.Relation.Kind,
            edge.Relation.Confidence,
            edge.Status,
            CanUseForJoin = edge.IsTrusted,
            Evidence = edge.Evidence.Select(e => new
            {
                Source = e.Kind,
                Path = e.SourcePath,
                e.Line,
                Reason = e.Detail,
            }),
            edge.ReviewReason,
        };
}

public sealed record RelationDecisionRequest(string Decision, string? Reason);

public sealed record PathRequest(string FromTableId, string ToTableId);
