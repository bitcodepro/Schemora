using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record RepairProposal(
    string Id,
    string WorkspaceId,
    string RecipeId,
    int BaseVersion,
    string Status,
    string BeforeSql,
    string AfterSql,
    int ReplacementCount,
    RecipeValidationResult Validation,
    string Reason,
    DateTimeOffset CreatedAt,
    RecipeDefinition ProposedDefinition,
    IReadOnlyDictionary<string, string> Snapshots,
    int? AppliedVersion = null,
    string? ContextRevision = null
);

public sealed record ProposeRepairRequest(
    string DatabaseId,
    string TableId,
    string OldColumn,
    string NewColumn,
    string Reason,
    int ExpectedVersion,
    string Kind = "column",
    string? OldSchema = null,
    string? OldTable = null
);

public sealed record ApproveRepairRequest(int ExpectedVersion);

public sealed record CompareDacpacRequest(string From, string? Path);

public sealed class ChangeService(
    CatalogStore store,
    RecipeService recipes,
    IRelationshipContext relationships,
    RecipeValidator validator,
    SchemaDiffer differ,
    RecipeColumnRewriter rewriter,
    DacpacReader dacpacReader
)
{
    private IReadOnlyDictionary<string, string> Snapshots(string workspaceId) =>
        store
            .All<DatabaseInfo>("Databases", workspaceId)
            .Where(d => d.CurrentSnapshotId is not null)
            .ToDictionary(d => d.Id, d => d.CurrentSnapshotId!);

    public object Diff(string databaseId, string from, string to)
    {
        var before = store.Get<SnapshotRecord>("SchemaSnapshots", from);
        var after = store.Get<SnapshotRecord>("SchemaSnapshots", to);
        if (
            before is null
            || after is null
            || before.DatabaseId != databaseId
            || after.DatabaseId != databaseId
            || before.WorkspaceId != after.WorkspaceId
        )
            throw new AppException("SNAPSHOT_NOT_FOUND", "Выберите два снимка одной БД.", 404);
        var result = differ.Compare(before.Graph, after.Graph);
        return new
        {
            DatabaseId = databaseId,
            FromSnapshotId = from,
            ToSnapshotId = to,
            result.Changes,
            result.RenameCandidates,
            result.Diagnostics,
        };
    }

    /// <summary>
    /// Compares the models of two databases of one workspace, e.g. the repository project against a DACPAC taken from another
    /// environment. Tables and columns are matched by schema and name; nothing is written.
    /// </summary>
    public object CompareSnapshots(string workspaceId, string from, string to)
    {
        var before = store.Get<SnapshotRecord>("SchemaSnapshots", from);
        var after = store.Get<SnapshotRecord>("SchemaSnapshots", to);
        if (before is null || after is null || before.WorkspaceId != workspaceId || after.WorkspaceId != workspaceId)
            throw new AppException("SNAPSHOT_NOT_FOUND", "Choose two snapshots of this workspace.", 404);
        return Comparison(before.Graph, after.Graph, before.DatabaseId, after.DatabaseId, null);
    }

    /// <summary>Compares a workspace snapshot with a DACPAC file that is only read for this comparison, not added to the catalog.</summary>
    public object CompareWithDacpac(string workspaceId, string from, string? path)
    {
        var before = store.Get<SnapshotRecord>("SchemaSnapshots", from);
        if (before is null || before.WorkspaceId != workspaceId)
            throw new AppException("SNAPSHOT_NOT_FOUND", "Choose a snapshot of this workspace.", 404);
        var file = LocalFiles.Validate(path, ".dacpac");
        return Comparison(before.Graph, dacpacReader.Read(file), before.DatabaseId, null, file);
    }

    private object Comparison(SchemaGraph before, SchemaGraph after, string fromDatabaseId, string? toDatabaseId, string? toPath)
    {
        var result = differ.Compare(before, after);
        return new
        {
            FromDatabaseId = fromDatabaseId,
            ToDatabaseId = toDatabaseId,
            ToPath = toPath,
            result.Changes,
            result.RenameCandidates,
            result.Diagnostics,
            // Text for the user to review and copy; nothing in Schemora executes it.
            MigrationDraft = MigrationDraft.Create(before, after).Sql,
        };
    }

    public IReadOnlyList<RepairProposal> Proposals(string recipeId)
    {
        var recipe = recipes.Require(recipeId);
        return store
            .All<RepairProposal>("RepairProposals", recipe.WorkspaceId)
            .Where(p => p.RecipeId == recipeId)
            .OrderByDescending(p => p.CreatedAt)
            .ToArray();
    }

    public async Task<RepairProposal> Propose(string id, ProposeRepairRequest request, CancellationToken ct)
    {
        var recipe = recipes.Require(id);
        if (recipe.Definition.Version != request.ExpectedVersion)
            throw new AppException("RECIPE_VERSION_CONFLICT", "Загрузите актуальную версию рецепта.", 409);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new AppException("REPAIR_REASON_REQUIRED", "Укажите основание замены (до 2000 символов).");
        if (!recipe.Definition.DatabaseIds.Contains(request.DatabaseId))
            throw new AppException("REPAIR_DATABASE_INVALID", "БД отсутствует в списке БД рецепта.");
        var revision = relationships.ContextRevision(recipe.WorkspaceId);
        var snapshots = Snapshots(recipe.WorkspaceId);
        var context = await relationships.GetSnapshot(recipe.WorkspaceId, ct);
        if (context.Revision != revision)
            throw new AppException(
                "REPAIR_CONTEXT_CHANGED",
                "Модель изменилась во время предварительного просмотра. Повторите действие.",
                409
            );
        var graph = context.Graph;
        if (request.Kind is not ("column" or "table" or "schema"))
            throw new AppException("REPAIR_KIND_INVALID", "Choose column, table or schema repair.");
        var rewritten =
            request.Kind == "column"
                ? rewriter.Preview(recipe.Definition, graph, request.DatabaseId, request.TableId, request.OldColumn, request.NewColumn)
                : rewriter.PreviewObjectRename(
                    recipe.Definition,
                    graph,
                    request.DatabaseId,
                    request.TableId,
                    request.OldSchema ?? "",
                    request.OldTable ?? ""
                );
        if (rewritten.ReplacementCount == 0 || rewritten.Diagnostics.Any(d => d.Severity == "error"))
            throw new AppException(
                "REPAIR_UNSAFE",
                "Не удалось однозначно связать замену. Исправьте этот SQL вручную.",
                422,
                System.Text.Json.JsonSerializer.Serialize(rewritten.Diagnostics, CatalogStore.Json)
            );
        var proposed = recipe.Definition with { SqlTemplate = rewritten.SqlTemplate, Dependencies = null };
        var validation = validator.Validate(proposed, graph);
        var proposal = new RepairProposal(
            Guid.NewGuid().ToString("N"),
            recipe.WorkspaceId,
            id,
            recipe.Definition.Version,
            "proposed",
            recipe.Definition.SqlTemplate,
            rewritten.SqlTemplate,
            rewritten.ReplacementCount,
            validation,
            request.Reason.Trim(),
            DateTimeOffset.UtcNow,
            proposed,
            snapshots,
            ContextRevision: revision
        );
        return store.WithWriteLock(() =>
        {
            if (
                recipes.Require(id).Definition.Version != recipe.Definition.Version
                || !Same(snapshots, Snapshots(recipe.WorkspaceId))
                || revision != relationships.ContextRevision(recipe.WorkspaceId)
            )
                throw new AppException("REPAIR_CONTEXT_CHANGED", "Схема или рецепт изменились; повторите предварительный просмотр.", 409);
            store.Put("RepairProposals", proposal.Id, proposal.WorkspaceId, proposal);
            return proposal;
        });
    }

    public async Task<RecipeEntry> Approve(string id, int expectedVersion, CancellationToken ct)
    {
        var proposal =
            store.Get<RepairProposal>("RepairProposals", id) ?? throw new AppException("REPAIR_NOT_FOUND", "Предложение не найдено.", 404);
        void Guard()
        {
            var latest =
                store.Get<RepairProposal>("RepairProposals", id)
                ?? throw new AppException("REPAIR_NOT_FOUND", "Предложение не найдено.", 404);
            if (latest.Status != "proposed")
                throw new AppException("REPAIR_ALREADY_DECIDED", "Решение по предложению уже принято.", 409);
            var current = recipes.Require(proposal.RecipeId);
            if (
                current.Definition.Version != expectedVersion
                || expectedVersion != proposal.BaseVersion
                || current.Definition.SqlTemplate != proposal.BeforeSql
            )
                throw new AppException(
                    "RECIPE_VERSION_CONFLICT",
                    "Рецепт изменился после предварительного просмотра. Создайте новое предложение.",
                    409
                );
            if (
                !Same(proposal.Snapshots, Snapshots(proposal.WorkspaceId))
                || proposal.ContextRevision is null
                || proposal.ContextRevision != relationships.ContextRevision(proposal.WorkspaceId)
            )
                throw new AppException(
                    "REPAIR_CONTEXT_CHANGED",
                    "Модель или связи изменились; создайте новый предварительный просмотр.",
                    409
                );
        }
        store.WithWriteLock(() =>
        {
            Guard();
            return true;
        });
        var approved = proposal with { Status = "approved", AppliedVersion = expectedVersion + 1 };
        // The final guard executes under the same write gate and transaction boundary as
        // the recipe/version/proposal batch, so rejection and stale previews cannot win a race.
        return await recipes.Save(
            proposal.WorkspaceId,
            proposal.ProposedDefinition with
            {
                Version = expectedVersion,
            },
            proposal.RecipeId,
            "Подтверждённое исправление: " + proposal.Reason,
            ct,
            true,
            [new("RepairProposals", id, proposal.WorkspaceId, approved)],
            Guard
        );
    }

    public RepairProposal Reject(string id)
    {
        return store.WithWriteLock(() =>
        {
            var proposal =
                store.Get<RepairProposal>("RepairProposals", id)
                ?? throw new AppException("REPAIR_NOT_FOUND", "Предложение не найдено.", 404);
            if (proposal.Status != "proposed")
                throw new AppException("REPAIR_ALREADY_DECIDED", "Решение уже принято.", 409);
            var rejected = proposal with { Status = "rejected" };
            store.Put("RepairProposals", id, proposal.WorkspaceId, rejected);
            return rejected;
        });
    }

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && value == p.Value);
}

public static class ChangeEndpoints
{
    public static void MapChangeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/databases/{id}/diff", (string id, string from, string to, ChangeService changes) => changes.Diff(id, from, to));
        app.MapGet(
            "/api/workspaces/{id}/compare",
            (string id, string from, string to, ChangeService changes) => changes.CompareSnapshots(id, from, to)
        );
        app.MapPost(
            "/api/workspaces/{id}/compare-dacpac",
            (string id, CompareDacpacRequest request, ChangeService changes) => changes.CompareWithDacpac(id, request.From, request.Path)
        );
        app.MapGet(
            "/api/workspaces/{id}/impact",
            (string id, RecipeService recipes) =>
                recipes
                    .List(id)
                    .Select(r => new
                    {
                        RecipeId = r.Definition.Id,
                        r.Definition.Name,
                        r.Definition.Version,
                        Status = r.Validation.Status,
                        r.Validation.Diagnostics,
                        r.LastCompatibleSnapshots,
                    })
        );
        app.MapGet("/api/recipes/{id}/repair-proposals", (string id, ChangeService changes) => changes.Proposals(id));
        app.MapPost(
            "/api/recipes/{id}/repair-proposals",
            (string id, ProposeRepairRequest request, ChangeService changes, CancellationToken ct) => changes.Propose(id, request, ct)
        );
        app.MapPost(
            "/api/repair-proposals/{id}/approve",
            (string id, ApproveRepairRequest request, ChangeService changes, CancellationToken ct) =>
                changes.Approve(id, request.ExpectedVersion, ct)
        );
        app.MapPost("/api/repair-proposals/{id}/reject", (string id, ChangeService changes) => changes.Reject(id));
    }
}
