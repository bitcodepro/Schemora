using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record ValueSet(string Id, string Name, IReadOnlyDictionary<string, string> Values, DateTimeOffset SavedAt);

public sealed record ValueSetSummary(string Id, string Name, IReadOnlyList<string> Inputs, DateTimeOffset SavedAt);

public sealed record SaveValueSetRequest(string Name, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Runtime values the user chose to keep under a name. They never enter the catalog: each recipe's sets are one file in
/// value-sets/ of the data directory, encrypted with Windows DPAPI for the current user and bound to the recipe id, so the
/// file is unreadable for other accounts, on other computers and when copied to another recipe. Portable exports,
/// catalog backups and diagnostics do not include the folder.
/// </summary>
public sealed class ValueSetStore(string dataDirectory)
{
    public const int MaximumSets = 50;
    public const int MaximumNameLength = 80;
    private readonly Lock gate = new();

    private string Folder => Path.Combine(dataDirectory, "value-sets");

    public IReadOnlyList<ValueSetSummary> List(string recipeId)
    {
        lock (gate)
            return Read(recipeId)
                .Select(set => new ValueSetSummary(set.Id, set.Name, set.Values.Keys.Order(StringComparer.Ordinal).ToArray(), set.SavedAt))
                .OrderBy(set => set.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
    }

    public ValueSet Get(string recipeId, string id)
    {
        lock (gate)
            return Read(recipeId).FirstOrDefault(set => set.Id == id) ?? throw NotFound();
    }

    /// <summary>Saving under an existing name replaces that set, so "Save" after editing values does what it says.</summary>
    public ValueSetSummary Save(string recipeId, RecipeDefinition recipe, SaveValueSetRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaximumNameLength)
            throw new AppException("VALUE_SET_NAME_INVALID", "Name the saved values with 1–80 characters.");
        if (request.Values is null || request.Values.Count == 0)
            throw new AppException("VALUE_SET_EMPTY", "There are no values to save.");
        var inputs = recipe.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        if (request.Values.Keys.Any(key => !inputs.Contains(key)))
            throw new AppException("VALUE_SET_INPUT_UNKNOWN", "Saved values name an input this recipe does not have.");
        if (request.Values.Values.Sum(value => (long)(value?.Length ?? 0)) > IdProcessor.MaximumCharacters)
            throw new AppException("VALUE_SET_TOO_LARGE", "Saved values exceed 8 Mi characters.");
        lock (gate)
        {
            var sets = Read(recipeId).ToList();
            var existing = sets.FindIndex(set => string.Equals(set.Name, name, StringComparison.CurrentCultureIgnoreCase));
            var saved = new ValueSet(
                existing >= 0 ? sets[existing].Id : Guid.NewGuid().ToString("N"),
                name,
                request.Values.ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.Ordinal),
                DateTimeOffset.UtcNow
            );
            if (existing >= 0)
                sets[existing] = saved;
            else if (sets.Count >= MaximumSets)
                throw new AppException("VALUE_SET_LIMIT", "A recipe keeps at most 50 saved value sets. Delete one first.");
            else
                sets.Add(saved);
            Write(recipeId, sets);
            return new(saved.Id, saved.Name, saved.Values.Keys.Order(StringComparer.Ordinal).ToArray(), saved.SavedAt);
        }
    }

    public void Delete(string recipeId, string id)
    {
        lock (gate)
        {
            var sets = Read(recipeId).ToList();
            if (sets.RemoveAll(set => set.Id == id) == 0)
                throw NotFound();
            Write(recipeId, sets);
        }
    }

    /// <summary>Removes the file without decrypting it, which also clears sets saved by another Windows account.</summary>
    public void DeleteAll(string recipeId)
    {
        lock (gate)
        {
            var path = FilePath(recipeId);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private IReadOnlyList<ValueSet> Read(string recipeId)
    {
        var path = FilePath(recipeId);
        if (!File.Exists(path))
            return [];
        LocalFiles.CheckNoLinks(path);
        byte[] json;
        try
        {
            json = Unprotect(File.ReadAllBytes(path), Entropy(recipeId));
        }
        catch (CryptographicException)
        {
            throw new AppException(
                "VALUE_SETS_UNREADABLE",
                "Saved values were encrypted by another Windows account or computer and cannot be read here.",
                409
            );
        }
        return JsonSerializer.Deserialize<ValueSet[]>(json, CatalogStore.Json) ?? [];
    }

    private void Write(string recipeId, IReadOnlyList<ValueSet> sets)
    {
        var path = FilePath(recipeId);
        if (sets.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Folder);
        LocalFiles.CheckNoLinks(Folder);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, Protect(JsonSerializer.SerializeToUtf8Bytes(sets, CatalogStore.Json), Entropy(recipeId)));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // Recipe ids are 32 hex digits; checking keeps the file inside the folder whatever the caller passes.
    private string FilePath(string recipeId) =>
        Guid.TryParseExact(recipeId, "N", out _)
            ? Path.Combine(Folder, recipeId.ToLowerInvariant() + ".dat")
            : throw new AppException("RECIPE_NOT_FOUND", "Рецепт не найден.", 404);

    private static byte[] Entropy(string recipeId) => Encoding.UTF8.GetBytes("Schemora value sets v1:" + recipeId.ToLowerInvariant());

    private static AppException NotFound() => new("VALUE_SET_NOT_FOUND", "These saved values no longer exist.", 404);

    private static byte[] Protect(byte[] data, byte[] entropy) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser) : throw Unavailable();

    private static byte[] Unprotect(byte[] data, byte[] entropy) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser) : throw Unavailable();

    private static AppException Unavailable() =>
        new("VALUE_SETS_UNAVAILABLE", "Saved values need Windows data protection, which this system does not provide.", 501);
}

public static class ValueSetEndpoints
{
    public static void MapValueSets(this WebApplication app)
    {
        app.MapGet(
            "/api/recipes/{id}/value-sets",
            (string id, RecipeService recipes, ValueSetStore sets) =>
            {
                recipes.Require(id);
                return sets.List(id);
            }
        );
        app.MapGet(
            "/api/recipes/{id}/value-sets/{setId}",
            (string id, string setId, RecipeService recipes, ValueSetStore sets) =>
            {
                recipes.Require(id);
                return sets.Get(id, setId);
            }
        );
        app.MapPut(
            "/api/recipes/{id}/value-sets",
            (string id, SaveValueSetRequest request, RecipeService recipes, ValueSetStore sets) =>
                sets.Save(id, recipes.Require(id).Definition, request)
        );
        app.MapDelete(
            "/api/recipes/{id}/value-sets/{setId}",
            (string id, string setId, RecipeService recipes, ValueSetStore sets) =>
            {
                recipes.Require(id);
                sets.Delete(id, setId);
                return Results.NoContent();
            }
        );
        app.MapDelete(
            "/api/recipes/{id}/value-sets",
            (string id, RecipeService recipes, ValueSetStore sets) =>
            {
                recipes.Require(id);
                sets.DeleteAll(id);
                return Results.NoContent();
            }
        );
    }
}
