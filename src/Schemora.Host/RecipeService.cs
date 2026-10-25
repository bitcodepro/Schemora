using System.Text.Json;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record RecipeEntry(
    string WorkspaceId,
    RecipeDefinition Definition,
    RecipeValidationResult Validation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string> LastCompatibleSnapshots,
    bool Archived = false
);

public sealed record RecipeVersionRecord(
    string Id,
    string RecipeId,
    int Version,
    RecipeDefinition Definition,
    DateTimeOffset CreatedAt,
    string Reason
);

public sealed record RecipeValidationRun(
    string Id,
    string WorkspaceId,
    string RecipeId,
    int RecipeVersion,
    DateTimeOffset ValidatedAt,
    RecipeValidationResult Result,
    IReadOnlyDictionary<string, string> Snapshots
);

public sealed partial class RecipeService(
    CatalogStore store,
    IRelationshipContext relationships,
    RecipeValidator validator,
    SqlRecipeRenderer renderer
)
{
    public RecipeEntry Require(string id) =>
        store.Get<RecipeEntry>("Recipes", id) ?? throw new AppException("RECIPE_NOT_FOUND", "Рецепт не найден.", 404);

    public IReadOnlyList<RecipeEntry> List(string workspaceId)
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        return store.All<RecipeEntry>("Recipes", workspaceId).Where(r => !r.Archived).OrderBy(r => r.Definition.Name).ToArray();
    }

    public IReadOnlyList<RecipeVersionRecord> Versions(string id)
    {
        var entry = Require(id);
        return store
            .All<RecipeVersionRecord>("RecipeVersions", entry.WorkspaceId)
            .Where(v => v.RecipeId == id)
            .OrderByDescending(v => v.Version)
            .ToArray();
    }

    private IReadOnlyDictionary<string, string> Snapshots(string workspaceId) =>
        store
            .All<DatabaseInfo>("Databases", workspaceId)
            .Where(d => d.CurrentSnapshotId is not null)
            .ToDictionary(d => d.Id, d => d.CurrentSnapshotId!);

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && value == p.Value);

    public async Task<RecipeEntry> Save(
        string workspaceId,
        RecipeDefinition input,
        string? existingId,
        string reason,
        CancellationToken ct,
        bool requireUsable = false,
        IReadOnlyList<StoredRecord>? extraRecords = null,
        Action? commitGuard = null
    )
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        if (
            string.IsNullOrWhiteSpace(input.Name)
            || input.Name.Length > 160
            || input.Description.Length > 8000
            || input.Tags.Count > 50
            || input.Tags.Any(t => t.Length > 80)
            || input.SqlTemplate.Length > 512 * 1024
        )
            throw new AppException("RECIPE_DEFINITION_INVALID", "Проверьте название, описание, теги и размер SQL-шаблона.");
        var revision = relationships.ContextRevision(workspaceId);
        var databases = store.All<DatabaseInfo>("Databases", workspaceId);
        if (input.DatabaseIds.Count == 0 || input.DatabaseIds.Any(id => databases.All(d => d.Id != id)))
            throw new AppException("RECIPE_DATABASE_INVALID", "Выберите существующие БД этой рабочей области.");
        var prior = existingId is not null ? Require(existingId) : null;
        if (prior is not null && prior.WorkspaceId != workspaceId)
            throw new AppException("RECIPE_WORKSPACE_MISMATCH", "Рецепт принадлежит другой рабочей области.");
        if (prior is not null && input.Version != prior.Definition.Version)
            throw new AppException("RECIPE_VERSION_CONFLICT", "Рецепт изменён в другом окне. Загрузите актуальную версию.", 409);
        var definition = input with
        {
            Id = prior?.Definition.Id ?? Guid.NewGuid().ToString("N"),
            Version = (prior?.Definition.Version ?? 0) + 1,
            Dependencies = null,
        };
        RecipeTemplate.ValidateDefinition(definition);
        var snapshots = Snapshots(workspaceId);
        var context = await relationships.GetSnapshot(workspaceId, ct);
        if (context.Revision != revision)
            throw new AppException("RELATION_CONTEXT_CHANGED", "Модель изменилась во время проверки. Повторите сохранение.", 409);
        var graph = context.Graph;
        var validation = Validate(definition, graph, databases);
        if (requireUsable && validation.Status is not ("Valid" or "Warning"))
            throw new AppException(
                "REPAIR_VALIDATION_FAILED",
                "Исправление ещё требует ручной проверки; версия не создана.",
                422,
                JsonSerializer.Serialize(validation.Diagnostics, CatalogStore.Json)
            );
        definition = definition with { Dependencies = validation.Dependencies };
        var now = DateTimeOffset.UtcNow;
        var entry = new RecipeEntry(
            workspaceId,
            definition,
            validation,
            prior?.CreatedAt ?? now,
            now,
            validation.Status is "Valid" or "Warning"
                ? snapshots.Where(p => definition.DatabaseIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)
                : prior?.LastCompatibleSnapshots ?? new Dictionary<string, string>()
        );
        return store.WithWriteLock(() =>
        {
            if (!Same(snapshots, Snapshots(workspaceId)))
                throw new AppException("SCHEMA_CHANGED", "Схема изменилась во время проверки. Повторите сохранение.", 409);
            if (revision != relationships.ContextRevision(workspaceId))
                throw new AppException(
                    "RELATION_CONTEXT_CHANGED",
                    "Связи или сопоставления источников изменились во время проверки. Повторите сохранение.",
                    409
                );
            if (prior is not null)
            {
                var current = Require(prior.Definition.Id);
                if (current.Definition.Version != prior.Definition.Version)
                    throw new AppException("RECIPE_VERSION_CONFLICT", "Рецепт изменён в другом окне. Загрузите актуальную версию.", 409);
                entry = entry with { Archived = current.Archived };
            }
            commitGuard?.Invoke();
            var version = new RecipeVersionRecord(
                definition.Id + ":v" + definition.Version,
                definition.Id,
                definition.Version,
                definition,
                now,
                reason
            );
            var run = new RecipeValidationRun(
                Guid.NewGuid().ToString("N"),
                workspaceId,
                definition.Id,
                definition.Version,
                now,
                validation,
                snapshots
            );
            store.Batch(
                new StoredRecord[]
                {
                    new("Recipes", definition.Id, workspaceId, entry),
                    new("RecipeVersions", version.Id, workspaceId, version),
                    new("RecipeInputs", definition.Id, workspaceId, new { RecipeId = definition.Id, definition.Inputs }),
                    new(
                        "RecipeDependencies",
                        definition.Id,
                        workspaceId,
                        new { RecipeId = definition.Id, Dependencies = validation.Dependencies }
                    ),
                    new("ValidationRuns", run.Id, workspaceId, run),
                }
                    .Concat(RegressionRecords(entry))
                    .Concat(extraRecords ?? [])
            );
            return entry;
        });
    }

    public RecipeEntry Archive(string id, bool archived) =>
        store.WithWriteLock(() =>
        {
            var current = Require(id);
            var updated = current with { Archived = archived, UpdatedAt = DateTimeOffset.UtcNow };
            store.Put("Recipes", id, current.WorkspaceId, updated);
            return updated;
        });

    public async Task<RecipeEntry> Clone(string id, string? name, CancellationToken ct)
    {
        var entry = Require(id);
        return await Save(
            entry.WorkspaceId,
            entry.Definition with
            {
                Name = string.IsNullOrWhiteSpace(name) ? entry.Definition.Name + " (копия)" : name,
            },
            null,
            "Копирование рецепта",
            ct
        );
    }

    public async Task<object> CompareVersion(string id, int version, CancellationToken ct)
    {
        var current = Require(id);
        var selected =
            Versions(id).FirstOrDefault(v => v.Version == version)
            ?? throw new AppException("RECIPE_VERSION_NOT_FOUND", "Version not found.", 404);
        var context = await relationships.GetSnapshot(current.WorkspaceId, ct);
        var databases = store.All<DatabaseInfo>("Databases", current.WorkspaceId);
        var recorded = store
            .All<RecipeValidationRun>("ValidationRuns", current.WorkspaceId)
            .Where(r => r.RecipeId == id && r.RecipeVersion == version)
            .OrderByDescending(r => r.ValidatedAt)
            .FirstOrDefault();
        if (
            context.Revision != relationships.ContextRevision(current.WorkspaceId)
            || Require(id).Definition.Version != current.Definition.Version
        )
            throw new AppException("SCHEMA_CHANGED", "Schema or recipe changed. Repeat comparison.", 409);
        return new
        {
            CurrentVersion = current.Definition.Version,
            SelectedVersion = version,
            Current = Validate(current.Definition, context.Graph, databases),
            Selected = Validate(selected.Definition, context.Graph, databases),
            LastRecorded = recorded?.Result,
            LastRecordedAt = recorded?.ValidatedAt,
        };
    }

    public async Task<RecipeEntry> Rollback(string id, int version, int expectedVersion, CancellationToken ct)
    {
        var current = Require(id);
        var source =
            Versions(id).FirstOrDefault(v => v.Version == version)
            ?? throw new AppException("RECIPE_VERSION_NOT_FOUND", "Версия не найдена.", 404);
        return await Save(current.WorkspaceId, source.Definition with { Version = expectedVersion }, id, "Откат к версии " + version, ct);
    }

    public async Task<RecipeValidationResult> Revalidate(string id, CancellationToken ct)
    {
        var entry = Require(id);
        var revision = relationships.ContextRevision(entry.WorkspaceId);
        var snapshots = Snapshots(entry.WorkspaceId);
        var context = await relationships.GetSnapshot(entry.WorkspaceId, ct);
        if (context.Revision != revision)
            throw new AppException("SCHEMA_CHANGED", "Модель изменилась во время проверки. Повторите действие.", 409);
        var graph = context.Graph;
        var result = CheckPriorCompatibility(
            entry,
            Validate(entry.Definition, graph, store.All<DatabaseInfo>("Databases", entry.WorkspaceId)),
            graph
        );
        store.WithWriteLock(() =>
        {
            var current = Require(id);
            if (
                current.Definition.Version != entry.Definition.Version
                || !Same(snapshots, Snapshots(entry.WorkspaceId))
                || revision != relationships.ContextRevision(entry.WorkspaceId)
            )
                throw new AppException("SCHEMA_CHANGED", "Модель, связи или рецепт изменились; повторите проверку.", 409);
            var updated = current with
            {
                Validation = result,
                LastCompatibleSnapshots = result.Status is "Valid" or "Warning"
                    ? snapshots.Where(p => entry.Definition.DatabaseIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)
                    : entry.LastCompatibleSnapshots,
            };
            var run = new RecipeValidationRun(
                Guid.NewGuid().ToString("N"),
                entry.WorkspaceId,
                id,
                entry.Definition.Version,
                DateTimeOffset.UtcNow,
                result,
                snapshots
            );
            store.Batch(
                new StoredRecord[]
                {
                    new("Recipes", id, entry.WorkspaceId, updated),
                    new("RecipeDependencies", id, entry.WorkspaceId, new { RecipeId = id, Dependencies = result.Dependencies }),
                    new("ValidationRuns", run.Id, entry.WorkspaceId, run),
                }.Concat(RegressionRecords(updated))
            );
            return true;
        });
        return result;
    }

    public async Task<int> RefreshWorkspace(string workspaceId, CancellationToken ct)
    {
        var count = 0;
        foreach (var entry in List(workspaceId))
        {
            ct.ThrowIfCancellationRequested();
            await Revalidate(entry.Definition.Id, ct);
            count++;
        }
        return count;
    }

    private RecipeValidationResult CheckPriorCompatibility(RecipeEntry entry, RecipeValidationResult result, RelationshipGraph graph)
    {
        if (result.Status == "Broken")
            return result;
        var diagnostics = result.Diagnostics.ToList();
        foreach (var database in entry.LastCompatibleSnapshots)
        {
            var old = store.Get<SnapshotRecord>("SchemaSnapshots", database.Value)?.Graph;
            if (old is null)
                continue;
            foreach (var objectId in result.Dependencies.Where(d => d.DatabaseId == database.Key).Select(d => d.TableId).Distinct())
            {
                var beforeObject = (old.Objects ?? []).FirstOrDefault(o => o.Id == objectId);
                var afterObject = (graph.ReadableObjects ?? [])
                    .FirstOrDefault(o => o.DatabaseId == database.Key && o.Object.Id == objectId)
                    ?.Object;
                var beforeKind = beforeObject?.Kind ?? (old.Tables.Any(t => t.Id == objectId) ? "table" : null);
                var afterKind =
                    afterObject?.Kind ?? (graph.Tables.Any(t => t.DatabaseId == database.Key && t.Table.Id == objectId) ? "table" : null);
                if (beforeKind is not null && afterKind is not null && beforeKind != afterKind)
                    diagnostics.Add(
                        new(
                            "warning",
                            "DEPENDENCY_OBJECT_KIND_CHANGED",
                            "Изменился вид используемого объекта: "
                                + beforeKind
                                + " → "
                                + afterKind
                                + ". Проверьте SQL и сохраните новую версию."
                        )
                    );
                if (
                    beforeObject is not null
                    && afterObject is not null
                    && (
                        beforeObject.Definition != afterObject.Definition
                        || beforeObject.Kind != afterObject.Kind
                        || beforeObject.Target != afterObject.Target
                    )
                )
                    diagnostics.Add(
                        new(
                            "warning",
                            "DEPENDENCY_OBJECT_CHANGED",
                            "Изменилось определение используемого объекта "
                                + afterObject.Schema
                                + "."
                                + afterObject.Name
                                + ". Проверьте SQL и сохраните новую версию."
                        )
                    );
            }
            foreach (var dependency in result.Dependencies.Where(d => d.DatabaseId == database.Key && d.ColumnName is not null).Distinct())
            {
                var before = (
                    old.Tables.FirstOrDefault(t => t.Id == dependency.TableId)?.Columns
                    ?? (old.Objects ?? []).FirstOrDefault(o => o.Id == dependency.TableId)?.Columns
                )?.FirstOrDefault(c => c.Name.Equals(dependency.ColumnName, StringComparison.OrdinalIgnoreCase));
                var after = (
                    graph.Tables.FirstOrDefault(t => t.DatabaseId == database.Key && t.Table.Id == dependency.TableId)?.Table.Columns
                    ?? (graph.ReadableObjects ?? [])
                        .FirstOrDefault(o => o.DatabaseId == database.Key && o.Object.Id == dependency.TableId)
                        ?.Object.Columns
                )?.FirstOrDefault(c => c.Name.Equals(dependency.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (
                    before is not null
                    && after is not null
                    && (Normalize(before.DataType) != Normalize(after.DataType) || before.IsNullable != after.IsNullable)
                )
                    diagnostics.Add(
                        new(
                            "warning",
                            "DEPENDENCY_TYPE_CHANGED",
                            $"Изменился тип/nullability используемой колонки {dependency.ColumnName}: {before.DataType} → {after.DataType}. Проверьте SQL и сохраните новую версию после проверки."
                        )
                    );
            }
        }
        return diagnostics.Count == result.Diagnostics.Count ? result : result with { Status = "NeedsReview", Diagnostics = diagnostics };
        static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    private RecipeValidationResult Validate(RecipeDefinition definition, RelationshipGraph graph, IReadOnlyList<DatabaseInfo> databases)
    {
        var result = validator.Validate(definition, graph);
        var missing = definition.DatabaseIds.Where(id => databases.All(d => d.Id != id)).ToArray();
        if (missing.Length > 0)
            return result with
            {
                Status = "Broken",
                Diagnostics = result
                    .Diagnostics.Append(
                        new SchemaDiagnostic(
                            "error",
                            "DATABASE_REMOVED",
                            "БД рецепта больше нет в текущем каталоге: " + string.Join(", ", missing)
                        )
                    )
                    .ToArray(),
            };
        if (
            result.Status != "Broken"
            && databases.Any(d => definition.DatabaseIds.Contains(d.Id) && d.Status is "failed" or "unavailable" or "pending")
        )
            return result with
            {
                Status = "NeedsReview",
                Diagnostics = result
                    .Diagnostics.Append(
                        new SchemaDiagnostic(
                            "warning",
                            "SCHEMA_STALE",
                            "Некоторые БД доступны только по последнему успешному снимку; обновите модель перед использованием SQL."
                        )
                    )
                    .ToArray(),
            };
        return result;
    }

    public async Task<RecipeRenderResult> Render(
        string id,
        IReadOnlyDictionary<string, string> inputs,
        string mode,
        string strategy,
        bool acknowledgeReview,
        CancellationToken ct,
        int? expectedVersion = null
    )
    {
        var entry = Require(id);
        // The UI renders the version it shows; a newer version saved elsewhere must be looked at before it produces SQL.
        if (expectedVersion is { } expected && expected != entry.Definition.Version)
            throw new AppException(
                "RECIPE_VERSION_STALE",
                $"The recipe is now at version {entry.Definition.Version}, not the version {expected} you are viewing. Reload it before generating SQL.",
                409
            );
        var revision = relationships.ContextRevision(entry.WorkspaceId);
        var snapshots = Snapshots(entry.WorkspaceId);
        var validation = await Revalidate(id, ct);
        if (validation.Status == "Broken")
            throw new AppException(
                "RECIPE_BROKEN",
                "Рецепт несовместим с текущей схемой. Исправьте ошибки до генерации SQL.",
                422,
                JsonSerializer.Serialize(validation.Diagnostics, CatalogStore.Json)
            );
        if (validation.Status == "NeedsReview" && !acknowledgeReview)
            throw new AppException(
                "RECIPE_REVIEW_REQUIRED",
                "Сначала проверьте предупреждения и явно подтвердите ручной SQL с непроверенными joins.",
                422,
                JsonSerializer.Serialize(validation.Diagnostics, CatalogStore.Json)
            );
        return store.WithWriteLock(() =>
        {
            if (
                Require(id).Definition.Version != entry.Definition.Version
                || !Same(snapshots, Snapshots(entry.WorkspaceId))
                || revision != relationships.ContextRevision(entry.WorkspaceId)
            )
                throw new AppException("RECIPE_CHANGED", "Рецепт, схема или связи изменились во время генерации. Повторите запрос.", 409);
            return renderer.Render(entry.Definition, inputs, mode, strategy) with { RecipeVersion = entry.Definition.Version };
        });
    }

    public async Task<object> SyntheticTest(string id, CancellationToken ct)
    {
        var entry = Require(id);
        var valueInputs = RecipeTemplate
            .ValidateDefinition(entry.Definition)
            .Where(token => token.Kind == "value")
            .Select(token => token.InputName)
            .ToHashSet(StringComparer.Ordinal);
        // Lists get a duplicate to exercise the duplicate policy; single-value inputs get one typed sample.
        var data = entry.Definition.Inputs.ToDictionary(
            i => i.Name,
            i =>
            {
                var sample = i.Type.ToLowerInvariant() switch
                {
                    "int" or "bigint" or "decimal" => "1,2,1",
                    "uniqueidentifier" => "00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000002",
                    "date" => "2026-01-01,2026-01-02,2026-01-01",
                    "datetime2" => "2026-01-01T08:00:00,2026-01-02T08:00:00",
                    "bit" => "1,0,1",
                    _ => "x,y,x",
                };
                return valueInputs.Contains(i.Name) ? sample.Split(',')[0] : sample;
            }
        );
        var optional = entry.Definition.Inputs.Where(i => i.Optional && valueInputs.Contains(i.Name)).Select(i => i.Name).ToArray();
        var checks = new List<string>();
        var diagnostics = new List<SchemaDiagnostic>();
        var validation = await Revalidate(id, ct);
        try
        {
            var rendered = renderer.Render(entry.Definition, data, "standalone", "auto");
            checks.Add("Синтетические typed inputs обработаны без ошибок");
            if (rendered.Statistics.All(p => p.Value.Invalid == 0 && p.Value.OutputCount > 0))
                checks.Add("Число значений соответствует выбранной duplicate policy");
            var again = renderer.Render(entry.Definition, data, "standalone", "auto");
            if (rendered.Sql != again.Sql)
                throw new InvalidOperationException("Детерминированность нарушена");
            checks.Add("Повторный render даёт идентичный SQL");
            if (optional.Length != 0)
            {
                renderer.Render(entry.Definition, data.Where(p => !optional.Contains(p.Key)).ToDictionary(), "standalone", "auto");
                checks.Add("Пустые необязательные значения дают NULL");
            }
        }
        catch (RecipeEngineException ex)
        {
            diagnostics.Add(new("error", ex.Code, ex.Message));
        }
        var result = new
        {
            Success = diagnostics.Count == 0 && validation.Status != "Broken",
            Checks = checks,
            Diagnostics = diagnostics.Concat(validation.Diagnostics),
            TestedAt = DateTimeOffset.UtcNow,
        };
        store.Put(
            "ValidationRuns",
            Guid.NewGuid().ToString("N"),
            entry.WorkspaceId,
            new
            {
                RecipeId = id,
                Kind = "synthetic",
                Result = result,
            }
        );
        return result;
    }
}
