using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record RecipeCollection(string Id, string Name, IReadOnlyList<string> RecipeIds, int Version = 0);

public sealed record CollectionSettings(IReadOnlyList<RecipeCollection> Collections);

public sealed record BatchRecipeCheck(string RecipeId, string Name, int Version, string Status, IReadOnlyList<string> DiagnosticCodes);

public sealed record BatchRecipeRun(
    string Id,
    string WorkspaceId,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string?> Snapshots,
    IReadOnlyList<BatchRecipeCheck> Checks
);

public sealed record BatchRecipeRequest(IReadOnlyList<string> RecipeIds);

public static class RecipeCollections
{
    public static CollectionSettings Read(CatalogStore store, string workspaceId)
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        return store.Get<CollectionSettings>("UserSettings", "recipe-collections:" + workspaceId) ?? new([]);
    }

    public static RecipeCollection Save(CatalogStore store, string workspaceId, RecipeCollection request) =>
        store.WithWriteLock(() =>
        {
            var settings = Read(store, workspaceId);
            if (
                string.IsNullOrWhiteSpace(request.Name)
                || request.Name.Length > 100
                || request.RecipeIds is null
                || request.RecipeIds.Count > 200
            )
                throw new AppException("COLLECTION_INVALID", "Use a collection name and at most 200 recipes.");
            var known = store.All<RecipeEntry>("Recipes", workspaceId).Where(r => !r.Archived).Select(r => r.Definition.Id).ToHashSet();
            if (request.RecipeIds.Any(id => !known.Contains(id)))
                throw new AppException("COLLECTION_RECIPE_INVALID", "Select active recipes in this workspace.");
            var prior = settings.Collections.FirstOrDefault(c => c.Id == request.Id);
            if (prior is not null && prior.Version != request.Version)
                throw new AppException("COLLECTION_CHANGED", "Reload the collection before saving.", 409);
            if (prior is null && settings.Collections.Count >= 100)
                throw new AppException("COLLECTION_LIMIT", "At most 100 collections per workspace.");
            var saved = request with
            {
                Id = prior?.Id ?? Guid.NewGuid().ToString("N"),
                Name = request.Name.Trim(),
                RecipeIds = request.RecipeIds.Distinct().ToArray(),
                Version = (prior?.Version ?? 0) + 1,
            };
            store.Put(
                "UserSettings",
                "recipe-collections:" + workspaceId,
                workspaceId,
                new CollectionSettings(settings.Collections.Where(c => c.Id != saved.Id).Append(saved).ToArray())
            );
            return saved;
        });

    public static object History(CatalogStore store, string workspaceId)
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        var runs = store.Recent<BatchRecipeRun>("RecipeBatchRuns", 2, workspaceId);
        var current = runs.FirstOrDefault();
        var previous = runs.Skip(1).FirstOrDefault();
        return new
        {
            Current = current,
            Previous = previous,
            Changes = current
                ?.Checks.Select(c => new
                {
                    c.RecipeId,
                    Before = previous?.Checks.FirstOrDefault(p => p.RecipeId == c.RecipeId)?.Status,
                    After = c.Status,
                    BeforeCodes = previous?.Checks.FirstOrDefault(p => p.RecipeId == c.RecipeId)?.DiagnosticCodes,
                    AfterCodes = c.DiagnosticCodes,
                })
                .ToArray(),
        };
    }

    public static async Task<object> Check(
        CatalogStore store,
        RecipeService service,
        RelationshipService relationships,
        string workspaceId,
        BatchRecipeRequest request,
        CancellationToken ct
    )
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        var ids = request.RecipeIds?.Distinct().ToArray() ?? [];
        if (ids.Length is < 1 or > 200)
            throw new AppException("BATCH_SIZE_INVALID", "Select 1–200 recipes.");
        var selected = ids.Select(service.Require).ToArray();
        if (selected.Any(r => r.WorkspaceId != workspaceId || r.Archived))
            throw new AppException("BATCH_WORKSPACE_INVALID", "Select active recipes in this workspace.");
        var revision = relationships.ContextRevision(workspaceId);
        var checks = new List<BatchRecipeCheck>();
        foreach (var entry in selected)
        {
            ct.ThrowIfCancellationRequested();
            var result = await service.Revalidate(entry.Definition.Id, ct);
            checks.Add(
                new(
                    entry.Definition.Id,
                    entry.Definition.Name,
                    entry.Definition.Version,
                    result.Status,
                    result.Diagnostics.Select(d => d.Code).Distinct().ToArray()
                )
            );
        }
        return store.WithWriteLock<object>(() =>
        {
            if (
                revision != relationships.ContextRevision(workspaceId)
                || selected.Any(e => service.Require(e.Definition.Id).Definition.Version != e.Definition.Version)
            )
                throw new AppException("BATCH_CONTEXT_CHANGED", "Schema or recipe changed during batch validation. Run again.", 409);
            var previous = store.Recent<BatchRecipeRun>("RecipeBatchRuns", 50, workspaceId).FirstOrDefault();
            var run = new BatchRecipeRun(
                Guid.NewGuid().ToString("N"),
                workspaceId,
                DateTimeOffset.UtcNow,
                store.All<DatabaseInfo>("Databases", workspaceId).ToDictionary(d => d.Id, d => d.CurrentSnapshotId),
                checks
            );
            store.Put("RecipeBatchRuns", run.Id, workspaceId, run);
            return new
            {
                Current = run,
                Previous = previous,
                Changes = checks.Select(c => new
                {
                    c.RecipeId,
                    Before = previous?.Checks.FirstOrDefault(p => p.RecipeId == c.RecipeId)?.Status,
                    After = c.Status,
                    BeforeCodes = previous?.Checks.FirstOrDefault(p => p.RecipeId == c.RecipeId)?.DiagnosticCodes,
                    AfterCodes = c.DiagnosticCodes,
                }),
            };
        });
    }
}
