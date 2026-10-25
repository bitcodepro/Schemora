using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Schemora.RecipeEngine;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record PortableTable(string DatabaseId, string Schema, string Name);

public sealed record PortableAssertion(
    string Id,
    string Kind,
    string Name,
    PortableTable From,
    PortableTable To,
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string> ToColumns,
    string Reason,
    string? Decision = null
);

public sealed record PortableProject(
    string Id,
    string Name,
    string DatabaseId,
    string? Configuration = null,
    string? TargetFramework = null
);

public sealed record PortableAlias(string DatabaseId, string Name, string Reason);

public sealed record PortableRegressionFixture(
    string RecipeId,
    string Name,
    IReadOnlyDictionary<string, string> ArtificialInputs,
    string Mode,
    string Strategy,
    IReadOnlyDictionary<string, RegressionExpectation> Expected,
    string ExpectedStrategy,
    IReadOnlyList<string> SqlContains,
    IReadOnlyList<string> SqlExcludes,
    string ExpectedSqlHash
);

public sealed record PendingPortableAssertion(
    string RecordKind,
    string Id,
    string WorkspaceId,
    PortableAssertion Assertion,
    string FromTableId,
    string ToTableId
);

public sealed record ConfigurationIssue(string Kind, string Id, string Status, string Message);

public sealed record ConfigurationPreview(string Revision, bool CanImport, IReadOnlyList<ConfigurationIssue> Items);

/// <summary>Imports declarations, never the source machine's trust or cached evidence.</summary>
public sealed class PortableConfigurationService(
    CatalogStore store,
    RecipeService recipes,
    RelationshipService relationships,
    ManualRelationService manual
)
{
    public ConfigurationBundle Export(string workspaceId)
    {
        var catalog = store.Catalog(workspaceId);
        var tables = catalog.Databases.SelectMany(d => (store.CurrentGraph(d.Id)?.Tables ?? []).Select(t => (d.Id, Table: t))).ToArray();
        PortableTable? Ref(string id)
        {
            var found = tables.FirstOrDefault(t => t.Table.Id == id);
            return found.Table is null ? HistoricalReference(workspaceId, id) : new(found.Id, found.Table.Schema, found.Table.Name);
        }
        var assertions = new List<PortableAssertion>();
        assertions.AddRange(Pending(workspaceId).Select(p => p.Assertion));
        foreach (var item in ManualRelationCatalog.List(store, workspaceId))
            if (Ref(item.FromTableId) is { } from && Ref(item.ToTableId) is { } to)
                assertions.Add(new(item.Id, "manual", item.Name, from, to, item.FromColumns, item.ToColumns, item.Reason));
        foreach (var item in store.All<SavedRelationDecision>("RelationOverrides", workspaceId))
            if (Ref(item.Original.Relation.FromTableId) is { } from && Ref(item.Original.Relation.ToTableId) is { } to)
                assertions.Add(
                    new(
                        item.RelationId,
                        "decision",
                        item.Original.Relation.Name,
                        from,
                        to,
                        item.Original.Relation.FromColumns,
                        item.Original.Relation.ToColumns,
                        item.Reason,
                        item.Decision
                    )
                );
        var projects = catalog
            .Sources.Where(s => s.Kind == "csharpProject")
            .Select(s =>
                (
                    Source: s,
                    Mapping: store.Get<EfProjectMapping>("UserSettings", "ef-map:" + workspaceId + ":" + s.Id)
                        ?? (catalog.Databases.Count == 1 ? new EfProjectMapping(s.Id, catalog.Databases[0].Id) : null)
                )
            )
            .Where(x => x.Mapping is not null)
            .Select(x =>
            {
                var settings = store.Get<CSharpProjectSettings>("UserSettings", CSharpProjectEndpoints.Key(workspaceId, x.Source.Id));
                return new PortableProject(
                    x.Source.Id,
                    Path.GetFileName(x.Source.Path),
                    x.Mapping!.DatabaseId,
                    settings?.Configuration,
                    settings?.TargetFramework
                );
            })
            .ToArray();
        var aliases = catalog
            .Databases.Select(d => (d.Id, Alias: store.Get<DatabaseAlias>("UserSettings", "db-name:" + d.Id)))
            .Where(x => x.Alias is not null)
            .Select(x => new PortableAlias(x.Id, x.Alias!.Name, x.Alias.Reason))
            .ToArray();
        return new(
            2,
            catalog.Workspace.Name,
            catalog.Workspace.Roots.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).ToArray(),
            catalog.Workspace.Excludes,
            catalog.Databases.Select(d => new PortableDatabase(d.Id, d.Name, d.SourceKind)).ToArray(),
            recipes.List(workspaceId).Select(r => r.Definition with { Dependencies = null }).ToArray(),
            null,
            assertions,
            projects,
            aliases,
            recipes
                .List(workspaceId)
                .SelectMany(r => recipes.RegressionFixtures(r.Definition.Id))
                .Select(f => new PortableRegressionFixture(
                    f.RecipeId,
                    f.Name,
                    f.ArtificialInputs,
                    f.Mode,
                    f.Strategy,
                    f.Expected,
                    f.ExpectedStrategy,
                    f.SqlContains,
                    f.SqlExcludes,
                    f.ExpectedSqlHash
                ))
                .ToArray()
        );
    }

    // Project only the referenced historical table identity; never deserialize historical graphs.
    private PortableTable? HistoricalReference(string workspaceId, string tableId)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = store.Path, Mode = SqliteOpenMode.ReadOnly }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT json_extract(s.Json,'$.databaseId'),json_extract(t.value,'$.schema'),json_extract(t.value,'$.name') FROM SchemaSnapshots s,json_each(s.Json,'$.graph.tables') t WHERE s.WorkspaceId=$workspace AND json_extract(t.value,'$.id')=$table ORDER BY json_extract(s.Json,'$.createdAt') DESC LIMIT 1";
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$table", tableId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public ConfigurationPreview Preview(string id, ImportConfigurationRequest request)
    {
        WorkspaceEndpoints.RequireWorkspace(store, id);
        var m = request.Manifest;
        if (
            m is null
            || m.FormatVersion is not (1 or 2)
            || m.Databases is null
            || m.Recipes is null
            || request.DatabaseMappings is null
            || m.Databases.Count > 100
            || m.Recipes.Count > 100
            || (m.Assertions?.Count ?? 0) > 1000
            || (m.Projects?.Count ?? 0) > 100
            || (m.Aliases?.Count ?? 0) > 100
            || (m.RegressionFixtures?.Count ?? 0) > 500
        )
            throw new AppException("CONFIGURATION_INVALID", "Expected configuration v1 or v2 within the supported item limits.");
        if (
            (m.Assertions ?? []).Any(a => a is null || a.From is null || a.To is null || a.FromColumns is null || a.ToColumns is null)
            || m.Recipes.Any(r => r is null || r.DatabaseIds is null)
        )
            throw new AppException("CONFIGURATION_INVALID", "Assertions and recipes must contain valid mappings.");
        var items = new List<ConfigurationIssue>();
        if (m.Recipes.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != m.Recipes.Count)
            items.Add(new("recipe", "identity", "conflict", "Source recipe identifiers must be unique."));
        var catalog = store.Catalog(id);
        var required = m
            .Databases.Select(d => d.Id)
            .Concat(m.Recipes.SelectMany(r => r.DatabaseIds))
            .Concat((m.Assertions ?? []).SelectMany(a => new[] { a.From.DatabaseId, a.To.DatabaseId }))
            .Concat((m.Projects ?? []).Select(p => p.DatabaseId))
            .Concat((m.Aliases ?? []).Select(a => a.DatabaseId))
            .Distinct();
        foreach (var source in required)
            if (!request.DatabaseMappings.TryGetValue(source, out var target) || !catalog.Databases.Any(d => d.Id == target))
                items.Add(new("database", source, "conflict", "Choose an explicit target database."));
        if (request.DatabaseMappings.Values.Distinct().Count() != request.DatabaseMappings.Count)
            items.Add(new("database", "mapping", "conflict", "Each source database requires a distinct target database."));
        if (
            (m.Projects ?? []).Select(p => p.Id).Distinct().Count() != (m.Projects?.Count ?? 0)
            || (request.ProjectMappings is not null && request.ProjectMappings.Values.Distinct().Count() != request.ProjectMappings.Count)
        )
            items.Add(new("project", "mapping", "conflict", "Each source project requires a distinct target project."));
        foreach (var project in m.Projects ?? [])
        {
            if (
                !Regex.IsMatch(project.Configuration ?? "Debug", @"^[A-Za-z0-9_. -]{1,64}$")
                || project.TargetFramework is { } framework && !Regex.IsMatch(framework, @"^[A-Za-z0-9_.-]{1,80}$")
            )
                items.Add(new("project", project.Id, "conflict", "Select a valid configuration and target framework."));
            if (
                request.ProjectMappings is null
                || !request.ProjectMappings.TryGetValue(project.Id, out var target)
                || !catalog.Sources.Any(s => s.Kind == "csharpProject" && s.Id == target)
            )
                items.Add(new("project", project.Id, "conflict", "Choose an explicit discovered target project."));
            else if (
                store.Get<EfProjectMapping>("UserSettings", "ef-map:" + id + ":" + target) is { } existing
                && request.DatabaseMappings.GetValueOrDefault(project.DatabaseId) != existing.DatabaseId
            )
                items.Add(new("project", project.Id, "conflict", "Target project already has a different database mapping."));
        }
        if ((m.Aliases ?? []).Select(a => a.DatabaseId).Distinct().Count() != (m.Aliases?.Count ?? 0))
            items.Add(new("alias", "mapping", "conflict", "Only one alias per source database is allowed."));
        foreach (var alias in m.Aliases ?? [])
        {
            if (
                string.IsNullOrWhiteSpace(alias.Name)
                || alias.Name.Length > 128
                || alias.Name.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(alias.Reason)
            )
                items.Add(new("alias", alias.DatabaseId, "conflict", "Alias needs a valid name and reason."));
            if (
                request.DatabaseMappings.TryGetValue(alias.DatabaseId, out var target)
                && store.Get<DatabaseAlias>("UserSettings", "db-name:" + target) is { } existing
                && existing.Name != alias.Name
            )
                items.Add(new("alias", alias.DatabaseId, "conflict", "Target database already has a different alias."));
        }
        foreach (var assertion in m.Assertions ?? [])
        {
            var from = Resolve(assertion.From, request);
            var to = Resolve(assertion.To, request);
            var valid =
                from is not null
                && to is not null
                && assertion.FromColumns.Count > 0
                && assertion.FromColumns.Count <= 32
                && assertion.FromColumns.Count == assertion.ToColumns.Count
                && !string.IsNullOrWhiteSpace(assertion.Reason)
                && assertion.Reason.Length <= 2000
                && assertion.Kind is "manual" or "decision"
                && (assertion.Kind != "decision" || assertion.Decision is "confirm" or "reject");
            if (valid)
                valid = RelationshipRules.Valid(
                    new("", assertion.Name, from!.Id, to!.Id, assertion.FromColumns, assertion.ToColumns, "user-confirmed", 1),
                    new[] { from, to }.DistinctBy(t => t!.Id).ToDictionary(t => t!.Id, t => new RelationshipTable("", "", t!))
                );
            items.Add(
                new(
                    assertion.Kind,
                    assertion.Id,
                    valid ? "review-required" : "conflict",
                    valid
                        ? "Imported evidence requires explicit review against the current schema."
                        : "Table or ordered column mapping is missing or incompatible."
                )
            );
        }
        foreach (var fixture in m.RegressionFixtures ?? [])
        {
            var valid =
                fixture is not null
                && m.Recipes.Any(r => r.Id == fixture.RecipeId)
                && !string.IsNullOrWhiteSpace(fixture.Name)
                && fixture.Name.Length <= 160
                && fixture.ArtificialInputs is not null
                && fixture.ArtificialInputs.All(p => p.Value is not null)
                && m.Recipes.First(r => r.Id == fixture.RecipeId).Inputs is { } recipeInputs
                && recipeInputs
                    .Select(i => i.Name)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(fixture.ArtificialInputs.Keys.Order(StringComparer.Ordinal))
                && fixture.ArtificialInputs.Values.Sum(v => (long)v.Length) <= 65536
                && fixture.Expected is not null
                && fixture
                    .Expected.Keys.Order(StringComparer.Ordinal)
                    .SequenceEqual(fixture.ArtificialInputs.Keys.Order(StringComparer.Ordinal))
                && fixture.Expected.Values.All(e =>
                    e is not null
                    && e.Statistics is not null
                    && e.Values is not null
                    && e.Ordinals is not null
                    && e.Values.Count <= 65536
                    && e.Values.All(v => v is not null && v.Length <= 65536)
                    && e.Ordinals.Count == e.Values.Count
                )
                && fixture.Mode is "standalone" or "parameterized"
                && fixture.Strategy is "auto" or "values" or "temp" or "openjson" or "list"
                && fixture.ExpectedSqlHash is { Length: 64 }
                && fixture.ExpectedSqlHash.All(Uri.IsHexDigit)
                && fixture.SqlContains is not null
                && fixture.SqlExcludes is not null
                && fixture.SqlContains.Count + fixture.SqlExcludes.Count <= 40
                && fixture.SqlContains.Concat(fixture.SqlExcludes).All(s => !string.IsNullOrEmpty(s) && s.Length <= 2000);
            items.Add(
                new(
                    "regression-fixture",
                    fixture?.Name ?? "",
                    valid ? "validate" : "conflict",
                    valid
                        ? "Artificial fixture definition will follow its newly imported recipe; past runs are excluded."
                        : "Fixture must reference an imported recipe and contain bounded artificial inputs and assertions."
                )
            );
        }
        foreach (var group in (m.RegressionFixtures ?? []).Where(f => f is not null).GroupBy(f => f.RecipeId))
            if (group.Count() > 50)
                items.Add(new("regression-fixture", group.Key, "conflict", "A recipe supports at most 50 fixtures."));
        foreach (var recipe in m.Recipes)
            items.Add(new("recipe", recipe.Id, "validate", "Recipe is saved as a new version and revalidated."));
        var revision = Identity.Hash(
            JsonSerializer.Serialize(
                new
                {
                    Context = relationships.ContextRevision(id),
                    request.Manifest,
                    request.DatabaseMappings,
                    request.ProjectMappings,
                },
                CatalogStore.Json
            )
        );
        return new(revision, items.All(i => i.Status != "conflict"), items);
    }

    private SchemaTable? Resolve(PortableTable table, ImportConfigurationRequest request) =>
        request.DatabaseMappings.TryGetValue(table.DatabaseId, out var db)
            ? store.CurrentGraph(db)?.Tables.SingleOrDefault(t => t.Schema == table.Schema && t.Name == table.Name)
            : null;

    public async Task<object> Import(string id, ImportConfigurationRequest request, CancellationToken ct)
    {
        var contextRevision = relationships.ContextRevision(id);
        var preview = Preview(id, request);
        if (!preview.CanImport)
            throw new AppException("CONFIGURATION_CONFLICT", "Resolve all preview conflicts before importing.", 409);
        if (request.Manifest.FormatVersion == 2 && request.PreviewRevision != preview.Revision)
            throw new AppException("CONFIGURATION_PREVIEW_REQUIRED", "Preview the current mappings before importing.", 409);
        store.WithWriteLock(() =>
        {
            if (relationships.ContextRevision(id) != contextRevision)
                throw new AppException("CONFIGURATION_CONTEXT_CHANGED", "Workspace changed; preview again.", 409);
            var records = new List<StoredRecord>();
            foreach (var a in request.Manifest.Assertions ?? [])
            {
                var key = Guid.NewGuid().ToString("N");
                records.Add(
                    new(
                        "UserSettings",
                        "portable-pending:" + key,
                        id,
                        new PendingPortableAssertion(
                            "portable-pending",
                            key,
                            id,
                            a,
                            Resolve(a.From, request)!.Id,
                            Resolve(a.To, request)!.Id
                        )
                    )
                );
            }
            foreach (var p in request.Manifest.Projects ?? [])
            {
                var target = request.ProjectMappings![p.Id];
                records.Add(
                    new(
                        "UserSettings",
                        "ef-map:" + id + ":" + target,
                        id,
                        new EfProjectMapping(target, request.DatabaseMappings[p.DatabaseId])
                    )
                );
                // Consent and evaluated MSBuild caches belong to this machine and never travel in a bundle.
                records.Add(
                    new(
                        "UserSettings",
                        CSharpProjectEndpoints.Key(id, target),
                        id,
                        new CSharpProjectSettings(target, p.Configuration ?? "Debug", p.TargetFramework, false)
                    )
                );
            }
            foreach (var a in request.Manifest.Aliases ?? [])
            {
                var db = store.Get<DatabaseInfo>("Databases", request.DatabaseMappings[a.DatabaseId])!;
                records.Add(new("UserSettings", "db-name:" + db.Id, id, new DatabaseAlias(a.Name.Trim(), a.Reason)));
                records.Add(new("Databases", db.Id, id, db with { Name = a.Name.Trim() }));
            }
            store.Batch(records);
            return true;
        });
        relationships.Invalidate(id);
        var results = new List<object>();
        var importedRecipes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in request.Manifest.Recipes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var saved = await recipes.Save(
                    id,
                    definition with
                    {
                        DatabaseIds = definition.DatabaseIds.Select(old => request.DatabaseMappings[old]).Distinct().ToArray(),
                    },
                    null,
                    "Portable configuration import",
                    ct
                );
                importedRecipes[definition.Id] = saved.Definition.Id;
                results.Add(
                    new
                    {
                        Name = definition.Name,
                        Id = saved.Definition.Id,
                        Status = saved.Validation.Status,
                    }
                );
            }
            catch (AppException ex)
            {
                results.Add(
                    new
                    {
                        Name = definition.Name,
                        Status = "failed",
                        ex.Code,
                        ex.Message,
                    }
                );
            }
            catch (RecipeEngineException ex)
            {
                results.Add(
                    new
                    {
                        Name = definition.Name,
                        Status = "failed",
                        ex.Code,
                        ex.Message,
                    }
                );
            }
        }
        foreach (var fixture in request.Manifest.RegressionFixtures ?? [])
        {
            if (!importedRecipes.TryGetValue(fixture.RecipeId, out var recipeId))
            {
                results.Add(
                    new
                    {
                        Name = fixture.Name,
                        Status = "failed",
                        Code = "FIXTURE_RECIPE_NOT_IMPORTED",
                    }
                );
                continue;
            }
            var key = Guid.NewGuid().ToString("N");
            store.Put(
                "RecipeRegressionFixtures",
                key,
                id,
                new RecipeRegressionFixture(
                    key,
                    recipeId,
                    id,
                    fixture.Name,
                    fixture.ArtificialInputs,
                    fixture.Mode,
                    fixture.Strategy,
                    fixture.Expected,
                    fixture.ExpectedStrategy,
                    fixture.SqlContains,
                    fixture.SqlExcludes,
                    DateTimeOffset.UtcNow,
                    fixture.ExpectedSqlHash
                )
            );
            results.Add(
                new
                {
                    Name = fixture.Name,
                    Id = key,
                    Status = "fixture-imported",
                }
            );
        }
        return new
        {
            Results = results,
            PendingReview = Pending(id).Count,
            RootSelectionRequired = false,
        };
    }

    public IReadOnlyList<PendingPortableAssertion> Pending(string id) =>
        store
            .All<System.Text.Json.JsonElement>("UserSettings", id)
            .Where(e => e.TryGetProperty("recordKind", out var kind) && kind.GetString() == "portable-pending")
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<PendingPortableAssertion>(e, CatalogStore.Json)!)
            .ToArray();

    public async Task Review(string id, string pendingId, bool apply, CancellationToken ct)
    {
        WorkspaceEndpoints.RequireWorkspace(store, id);
        var pending = store.Get<PendingPortableAssertion>("UserSettings", "portable-pending:" + pendingId);
        if (pending is null || pending.WorkspaceId != id)
            throw new AppException("CONFIGURATION_REVIEW_NOT_FOUND", "Pending assertion not found.", 404);
        if (apply)
        {
            var a = pending.Assertion;
            if (a.Kind == "manual")
                await manual.Save(id, new(a.Name, pending.FromTableId, pending.ToTableId, a.FromColumns, a.ToColumns, a.Reason), null, ct);
            else
            {
                var relation = new SchemaRelation(
                    "",
                    a.Name,
                    pending.FromTableId,
                    pending.ToTableId,
                    a.FromColumns,
                    a.ToColumns,
                    "candidate",
                    0
                );
                await relationships.Decide(
                    id,
                    RelationshipRules.Identity(relation),
                    a.Decision == "confirm" ? "confirmed" : "rejected",
                    a.Reason,
                    ct
                );
            }
        }
        store.Delete("UserSettings", "portable-pending:" + pendingId);
    }
}
