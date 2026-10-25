using Schemora.Workspaces;

namespace Schemora.Host;

public static class EnhancementEndpoints
{
    public static void MapEnhancementEndpoints(this WebApplication app)
    {
        app.MapPost(
            "/api/schema/build/plan",
            (ImportRequest request, SqlProjectBuilder builder, CancellationToken ct) => builder.Plan(request.Path, ct)
        );
        app.MapGet(
            "/api/build-manifests/{manifestId}",
            (string manifestId, SqlProjectBuilder builder) =>
                builder.ReadManifest(manifestId) ?? throw new AppException("BUILD_MANIFEST_NOT_FOUND", "Манифест сборки не найден.", 404)
        );
        app.MapGet(
            "/api/databases/{databaseId}/build-manifest",
            (string databaseId, CatalogStore store, SqlProjectBuilder builder) =>
            {
                var database =
                    store.Get<DatabaseInfo>("Databases", databaseId) ?? throw new AppException("DATABASE_NOT_FOUND", "БД не найдена.", 404);
                var snapshot = database.CurrentSnapshotId is { } snapshotId
                    ? store.Get<SnapshotRecord>("SchemaSnapshots", snapshotId)
                    : null;
                return snapshot?.BuildManifestId is { } manifestId
                    ? Results.Ok(builder.ReadManifest(manifestId))
                    : Results.NotFound(
                        new { Code = "BUILD_MANIFEST_NOT_FOUND", Message = "У этого снимка нет проверенного манифеста сборки." }
                    );
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/manual-relations",
            (string id, ManualRelationService service, CancellationToken ct) => service.List(id, ct)
        );
        app.MapPost(
            "/api/workspaces/{id}/manual-relations",
            (string id, ManualRelationRequest request, ManualRelationService service, CancellationToken ct) =>
                service.Save(id, request, null, ct)
        );
        app.MapPut(
            "/api/workspaces/{id}/manual-relations/{manualId}",
            (string id, string manualId, ManualRelationRequest request, ManualRelationService service, CancellationToken ct) =>
                service.Save(id, request, manualId, ct)
        );
        app.MapDelete(
            "/api/workspaces/{id}/manual-relations/{manualId}",
            (string id, string manualId, int expectedVersion, ManualRelationService service) =>
            {
                service.Delete(id, manualId, expectedVersion);
                return Results.NoContent();
            }
        );
        app.MapGet(
            "/api/workspaces/{id}/tables/{tableId}",
            async (string id, string tableId, RelationshipService service, CancellationToken ct) =>
            {
                var graph = await service.Get(id, ct);
                var table =
                    graph.Tables.FirstOrDefault(t => t.Table.Id == tableId)
                    ?? throw new AppException("TABLE_NOT_FOUND", "Таблица не найдена.", 404);
                return new
                {
                    table.DatabaseId,
                    table.DatabaseName,
                    table.Table.Id,
                    table.Table.Schema,
                    table.Table.Name,
                    table.Table.Columns,
                    table.Table.PrimaryKey,
                    table.Table.UniqueKeys,
                };
            }
        );
        app.MapPost(
            "/api/workspaces/{id}/recipe-preview",
            (string id, ChainRecipeRequest request, ChainRecipeBuilder service, CancellationToken ct) => service.Preview(id, request, ct)
        );
        app.MapGet("/api/workspaces/{id}/auto-refresh", (string id, AutoRefreshService service) => service.Get(id));
        app.MapPut(
            "/api/workspaces/{id}/auto-refresh",
            (string id, AutoRefreshSettings request, AutoRefreshService service) => service.Update(id, request)
        );
        app.MapGet(
            "/api/workspaces/{id}/objects",
            (
                string id,
                string? databaseId,
                string? kind,
                string? query,
                int? offset,
                int? limit,
                string? snapshotId,
                SchemaObjectService service
            ) => service.List(id, databaseId, kind, query, offset ?? 0, limit ?? 100, snapshotId)
        );
        app.MapGet(
            "/api/workspaces/{id}/objects/{objectId}",
            (string id, string objectId, string? snapshotId, SchemaObjectService service) => service.Detail(id, objectId, snapshotId)
        );
    }
}
