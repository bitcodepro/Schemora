using System.Text;
using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record CollectionRenderRequest(IReadOnlyDictionary<string, string>? InputText, string? Strategy, bool AcknowledgeReview);

public sealed record CollectionRecipeResult(
    string RecipeId,
    string Name,
    int Version,
    IReadOnlyList<string> Inputs,
    bool Skipped,
    string? Code,
    string? Message,
    IReadOnlyList<string> Warnings
);

/// <summary>The combined script exists only when every active recipe rendered; a partial script could be run by mistake.</summary>
public sealed record CollectionRenderResult(string? Sql, IReadOnlyList<CollectionRecipeResult> Recipes);

public static class RecipeScripts
{
    /// <summary>
    /// One script for several rendered recipes. Each recipe is its own batch (GO), because recipes that share an input name
    /// create temp tables of the same name, which SQL Server refuses twice in one batch.
    /// </summary>
    public static string Combine(IReadOnlyList<(string Name, int Version, string Sql)> parts)
    {
        if (parts.Sum(part => (long)part.Sql.Length) > 32 * 1024 * 1024)
            throw new RecipeEngineException("SQL_OUTPUT_TOO_LARGE", "Generated SQL exceeds 64 MiB.");
        var script = new StringBuilder("-- Schemora: ")
            .Append(parts.Count)
            .Append(" recipes rendered together. Each runs in its own batch (GO); review before executing.\n");
        for (var index = 0; index < parts.Count; index++)
            script
                .Append("\n-- ")
                .Append(index + 1)
                .Append('/')
                .Append(parts.Count)
                .Append(": ")
                .Append(parts[index].Name.ReplaceLineEndings(" "))
                .Append(" (v")
                .Append(parts[index].Version)
                .Append(")\n")
                .Append(parts[index].Sql.TrimEnd())
                .Append("\nGO\n");
        return script.ToString();
    }
}

public static class CollectionRender
{
    public static async Task<CollectionRenderResult> Render(
        CatalogStore store,
        RecipeService recipes,
        string workspaceId,
        string collectionId,
        CollectionRenderRequest request,
        CancellationToken ct
    )
    {
        var collection =
            RecipeCollections.Read(store, workspaceId).Collections.FirstOrDefault(c => c.Id == collectionId)
            ?? throw new AppException("COLLECTION_NOT_FOUND", "Collection not found.", 404);
        var entries = collection.RecipeIds.Select(recipes.Require).ToArray();
        var active = entries.Where(entry => !entry.Archived).ToArray();
        if (active.Length == 0)
            throw new AppException("COLLECTION_EMPTY", "The collection has no active recipes.");
        // Inputs are shared by name: one ID list feeds every recipe that has an input called that.
        var supplied = request.InputText ?? new Dictionary<string, string>();
        var used = active.SelectMany(entry => entry.Definition.Inputs).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        if (supplied.Keys.Any(key => !used.Contains(key)))
            throw new RecipeEngineException("INPUT_UNKNOWN", "Values were supplied for an undefined recipe input.");
        if (supplied.Values.Sum(value => (long)(value?.Length ?? 0)) > IdProcessor.MaximumCharacters)
            throw new RecipeEngineException("ID_INPUT_TOO_LARGE", "Combined recipe input exceeds 8 Mi characters.");
        var results = new List<CollectionRecipeResult>();
        var parts = new List<(string Name, int Version, string Sql)>();
        foreach (var entry in entries)
        {
            var definition = entry.Definition;
            var names = definition.Inputs.Select(input => input.Name).ToArray();
            if (entry.Archived)
            {
                results.Add(new(definition.Id, definition.Name, definition.Version, names, true, null, null, []));
                continue;
            }
            var inputs = supplied
                .Where(pair => names.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.Ordinal);
            try
            {
                var rendered = await recipes.Render(
                    definition.Id,
                    inputs,
                    "standalone",
                    request.Strategy ?? "auto",
                    request.AcknowledgeReview,
                    ct
                );
                parts.Add((definition.Name, definition.Version, rendered.Sql));
                results.Add(new(definition.Id, definition.Name, definition.Version, names, false, null, null, rendered.Warnings));
            }
            catch (AppException failure)
            {
                results.Add(new(definition.Id, definition.Name, definition.Version, names, false, failure.Code, failure.Message, []));
            }
            catch (RecipeEngineException failure)
            {
                results.Add(new(definition.Id, definition.Name, definition.Version, names, false, failure.Code, failure.Message, []));
            }
        }
        return new(parts.Count == active.Length ? RecipeScripts.Combine(parts) : null, results);
    }
}
