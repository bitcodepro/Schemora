using System.Text.Json;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record ManualRelationRequest(
    string Name,
    string FromTableId,
    string ToTableId,
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string> ToColumns,
    string Reason,
    int? ExpectedVersion = null
);

public sealed record ManualColumnShape(string Name, string DataType, bool IsNullable);

public sealed record ManualRelationRecord(
    string RecordKind,
    string Id,
    string WorkspaceId,
    int Version,
    string Name,
    string FromTableId,
    string ToTableId,
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string> ToColumns,
    string Reason,
    IReadOnlyList<ManualColumnShape> FromShape,
    IReadOnlyList<ManualColumnShape> ToShape,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public SchemaRelation Relation => new("", Name, FromTableId, ToTableId, FromColumns, ToColumns, "user-confirmed", 1);
}

public sealed record ManualRelationView(
    string Id,
    string WorkspaceId,
    int Version,
    string Name,
    string FromTableId,
    string ToTableId,
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string> ToColumns,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RelationId,
    string Status,
    bool CanUseForJoin
);

public static class ManualRelationCatalog
{
    public static IReadOnlyList<ManualRelationRecord> List(CatalogStore store, string workspaceId) =>
        store
            .All<JsonElement>("UserSettings", workspaceId)
            .Where(e => e.TryGetProperty("recordKind", out var kind) && kind.GetString() == "manual-relation")
            .Select(e => e.Deserialize<ManualRelationRecord>(CatalogStore.Json)!)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();

    public static string Key(string id) => "manual-relation:" + id;

    public static bool IsCurrent(ManualRelationRecord record, IReadOnlyList<RelationshipTable> tables)
    {
        var from = tables.FirstOrDefault(t => t.Table.Id == record.FromTableId)?.Table;
        var to = tables.FirstOrDefault(t => t.Table.Id == record.ToTableId)?.Table;
        return from is not null && to is not null && ShapeMatches(from, record.FromShape) && ShapeMatches(to, record.ToShape);
    }

    private static bool ShapeMatches(SchemaTable table, IReadOnlyList<ManualColumnShape> shapes) =>
        shapes.All(s =>
            table.Columns.FirstOrDefault(c => c.Name == s.Name) is { } column
            && column.DataType == s.DataType
            && column.IsNullable == s.IsNullable
        );

    public static RelationshipEdge Edge(ManualRelationRecord record, bool current) =>
        new(
            record.Relation with
            {
                Id = RelationshipRules.Identity(record.Relation),
            },
            current ? "confirmed" : "stale",
            current,
            [new("manual-relation", "", null, $"{record.Name} (v{record.Version}): {record.Reason}")],
            current ? record.Reason : "Колонки или их типы изменились. Откройте ручную связь и подтвердите новое сопоставление."
        );
}

public sealed class ManualRelationService(CatalogStore store, RelationshipService relationships)
{
    public async Task<IReadOnlyList<ManualRelationView>> List(string workspaceId, CancellationToken ct)
    {
        var graph = await relationships.Get(workspaceId, ct);
        return ManualRelationCatalog.List(store, workspaceId).Select(r => View(r, graph)).ToArray();
    }

    public async Task<ManualRelationView> Save(string workspaceId, ManualRelationRequest request, string? id, CancellationToken ct)
    {
        if (
            string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 160
            || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 2000
        )
            throw new AppException("MANUAL_RELATION_INVALID", "Нужны название (до 160 символов) и основание связи (до 2000 символов).");
        if (
            request.FromColumns is null
            || request.ToColumns is null
            || request.FromColumns.Count is < 1 or > 32
            || request.FromColumns.Count != request.ToColumns.Count
            || request.FromColumns.Distinct(StringComparer.Ordinal).Count() != request.FromColumns.Count
            || request.ToColumns.Distinct(StringComparer.Ordinal).Count() != request.ToColumns.Count
            || request.FromColumns.Any(string.IsNullOrWhiteSpace)
            || request.ToColumns.Any(string.IsNullOrWhiteSpace)
        )
            throw new AppException("MANUAL_RELATION_COLUMNS_INVALID", "Выберите от 1 до 32 уникальных упорядоченных пар колонок.");
        var context = await relationships.GetSnapshot(workspaceId, ct);
        var from = context.Graph.Tables.FirstOrDefault(t => t.Table.Id == request.FromTableId);
        var to = context.Graph.Tables.FirstOrDefault(t => t.Table.Id == request.ToTableId);
        if (from is null || to is null)
            throw new AppException("TABLE_NOT_FOUND", "Таблицы должны входить в текущую рабочую область.", 404);
        var databases = store.All<DatabaseInfo>("Databases", workspaceId);
        if (databases.Any(d => (d.Id == from.DatabaseId || d.Id == to.DatabaseId) && d.Status is "failed" or "unavailable" or "pending"))
            throw new AppException("SCHEMA_STALE", "Обновите схему перед сохранением ручной связи.", 409);
        var relation = new SchemaRelation(
            "",
            request.Name.Trim(),
            request.FromTableId,
            request.ToTableId,
            request.FromColumns,
            request.ToColumns,
            "user-confirmed",
            1
        );
        if (!RelationshipRules.Valid(relation, context.Graph.Tables.ToDictionary(t => t.Table.Id, StringComparer.Ordinal)))
            throw new AppException(
                "MANUAL_RELATION_TYPES_INVALID",
                "Каждая пара должна ссылаться на существующие колонки с совместимыми типами."
            );
        if (relation.FromTableId == relation.ToTableId && relation.FromColumns.SequenceEqual(relation.ToColumns))
            throw new AppException("MANUAL_RELATION_IDENTITY", "Связь колонки с самой собой не задаёт JOIN.");
        var now = DateTimeOffset.UtcNow;
        var saved = store.WithWriteLock(() =>
        {
            if (relationships.ContextRevision(workspaceId) != context.Revision)
                throw new AppException("RELATION_CONTEXT_CHANGED", "Модель изменилась; обновите форму и повторите сохранение.", 409);
            var previous = id is null ? null : store.Get<ManualRelationRecord>("UserSettings", ManualRelationCatalog.Key(id));
            if (id is not null && (previous is null || previous.WorkspaceId != workspaceId))
                throw new AppException("MANUAL_RELATION_NOT_FOUND", "Ручная связь не найдена.", 404);
            if (previous is not null && request.ExpectedVersion != previous.Version)
                throw new AppException("MANUAL_RELATION_VERSION_CONFLICT", "Связь изменена в другом окне. Загрузите текущую версию.", 409);
            var duplicate = ManualRelationCatalog
                .List(store, workspaceId)
                .FirstOrDefault(r => r.Id != id && RelationshipRules.Identity(r.Relation) == RelationshipRules.Identity(relation));
            if (duplicate is not null)
                throw new AppException(
                    "MANUAL_RELATION_EXISTS",
                    "Такое сопоставление уже существует. Откройте сохранённую ручную связь.",
                    409
                );
            var record = new ManualRelationRecord(
                "manual-relation",
                id ?? Guid.NewGuid().ToString("N"),
                workspaceId,
                (previous?.Version ?? 0) + 1,
                relation.Name,
                relation.FromTableId,
                relation.ToTableId,
                relation.FromColumns,
                relation.ToColumns,
                request.Reason.Trim(),
                Shape(from.Table, relation.FromColumns),
                Shape(to.Table, relation.ToColumns),
                previous?.CreatedAt ?? now,
                now
            );
            // A previous rejection/confirmation applies to a canonical mapping, not a newly edited manual assertion.
            // Keep it visible; the normal decision workflow resolves conflicts explicitly.
            if (previous is not null && RelationshipRules.Identity(previous.Relation) != RelationshipRules.Identity(record.Relation))
                RemoveOwnedDecision(previous);
            store.Put("UserSettings", ManualRelationCatalog.Key(record.Id), workspaceId, record);
            return record;
        });
        relationships.Invalidate(workspaceId);
        return View(saved, await relationships.Get(workspaceId, ct));
    }

    public void Delete(string workspaceId, string id, int expectedVersion)
    {
        store.WithWriteLock(() =>
        {
            WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
            var record = store.Get<ManualRelationRecord>("UserSettings", ManualRelationCatalog.Key(id));
            if (record is null || record.WorkspaceId != workspaceId)
                throw new AppException("MANUAL_RELATION_NOT_FOUND", "Ручная связь не найдена.", 404);
            if (record.Version != expectedVersion)
                throw new AppException("MANUAL_RELATION_VERSION_CONFLICT", "Связь изменена в другом окне.", 409);
            RemoveOwnedDecision(record);
            store.Delete("UserSettings", ManualRelationCatalog.Key(id));
            return true;
        });
        relationships.Invalidate(workspaceId);
    }

    private void RemoveOwnedDecision(ManualRelationRecord record)
    {
        var relationId = RelationshipRules.Identity(record.Relation);
        var decision = store.Get<SavedRelationDecision>("RelationOverrides", relationId);
        if (decision?.Original.Evidence.Any(evidence => evidence.Kind == "manual-relation") == true)
            store.Delete("RelationOverrides", relationId);
    }

    private static IReadOnlyList<ManualColumnShape> Shape(SchemaTable table, IReadOnlyList<string> names) =>
        names
            .Select(n => table.Columns.Single(c => c.Name == n))
            .Select(c => new ManualColumnShape(c.Name, c.DataType, c.IsNullable))
            .ToArray();

    private static ManualRelationView View(ManualRelationRecord record, RelationshipGraph graph)
    {
        var relationId = RelationshipRules.Identity(record.Relation);
        var edge = graph.Relations.FirstOrDefault(e => e.Relation.Id == relationId);
        var current = ManualRelationCatalog.IsCurrent(record, graph.Tables);
        return new(
            record.Id,
            record.WorkspaceId,
            record.Version,
            record.Name,
            record.FromTableId,
            record.ToTableId,
            record.FromColumns,
            record.ToColumns,
            record.Reason,
            record.CreatedAt,
            record.UpdatedAt,
            relationId,
            current ? edge?.Status ?? "stale" : "stale",
            current && edge?.IsTrusted == true
        );
    }
}
