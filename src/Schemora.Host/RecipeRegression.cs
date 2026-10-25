using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record RegressionExpectation(
    IdStatistics Statistics,
    IReadOnlyList<string> Values,
    IReadOnlyList<int> Ordinals,
    bool Deduplicate,
    bool PreserveOrder
);

public sealed record RecipeRegressionFixture(
    string Id,
    string RecipeId,
    string WorkspaceId,
    string Name,
    IReadOnlyDictionary<string, string> ArtificialInputs,
    string Mode,
    string Strategy,
    IReadOnlyDictionary<string, RegressionExpectation> Expected,
    string ExpectedStrategy,
    IReadOnlyList<string> SqlContains,
    IReadOnlyList<string> SqlExcludes,
    DateTimeOffset CreatedAt,
    string ExpectedSqlHash
);

public sealed record SaveRegressionRequest(
    string Name,
    int ExpectedVersion,
    bool ArtificialDataConfirmed,
    IReadOnlyDictionary<string, string> ArtificialInputs,
    string Mode = "standalone",
    string Strategy = "auto",
    IReadOnlyList<string>? SqlContains = null,
    IReadOnlyList<string>? SqlExcludes = null
);

public sealed record RecipeRegressionRun(
    string Id,
    string FixtureId,
    string RecipeId,
    int RecipeVersion,
    DateTimeOffset TestedAt,
    bool Success,
    IReadOnlyList<string> Failures
);

public sealed partial class RecipeService
{
    public IReadOnlyList<RecipeRegressionFixture> RegressionFixtures(string id)
    {
        var entry = Require(id);
        return store
            .All<RecipeRegressionFixture>("RecipeRegressionFixtures", entry.WorkspaceId)
            .Where(f => f.RecipeId == id)
            .OrderBy(f => f.CreatedAt)
            .ToArray();
    }

    public IReadOnlyList<RecipeRegressionRun> RegressionRuns(string id)
    {
        var entry = Require(id);
        return store
            .All<RecipeRegressionRun>("RecipeRegressionRuns", entry.WorkspaceId)
            .Where(r => r.RecipeId == id)
            .OrderByDescending(r => r.TestedAt)
            .Take(100)
            .ToArray();
    }

    // Only this explicit endpoint accepts saved input data. Runtime rendering never calls it.
    public RecipeRegressionFixture SaveRegression(string id, SaveRegressionRequest request)
    {
        if (!request.ArtificialDataConfirmed)
            throw new AppException("REGRESSION_ARTIFICIAL_REQUIRED", "Confirm that fixture values are artificial test data.");
        if (
            string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 160
            || request.ArtificialInputs is null
            || request.ArtificialInputs.Any(p => p.Value is null)
            || request.ArtificialInputs.Values.Sum(v => (long)v.Length) > 65536
        )
            throw new AppException(
                "REGRESSION_FIXTURE_INVALID",
                "Use a name up to 160 characters and artificial input up to 64 Ki characters."
            );
        if (
            (request.SqlContains?.Count ?? 0) + (request.SqlExcludes?.Count ?? 0) > 40
            || (request.SqlContains ?? []).Concat(request.SqlExcludes ?? []).Any(s => string.IsNullOrEmpty(s) || s.Length > 2000)
        )
            throw new AppException("REGRESSION_ASSERTION_INVALID", "Use at most 40 nonempty SQL assertions up to 2000 characters each.");
        return store.WithWriteLock(() =>
        {
            var entry = Require(id);
            if (entry.Definition.Version != request.ExpectedVersion)
                throw new AppException("RECIPE_VERSION_CONFLICT", "Reload the saved recipe before saving a fixture.", 409);
            if (RegressionFixtures(id).Count >= 50)
                throw new AppException("REGRESSION_FIXTURE_LIMIT", "A recipe supports at most 50 saved fixtures.");
            var rendered = renderer.Render(entry.Definition, request.ArtificialInputs, request.Mode, request.Strategy);
            var expected = entry.Definition.Inputs.ToDictionary(
                i => i.Name,
                i =>
                {
                    var parsed = new IdProcessor().Parse(request.ArtificialInputs[i.Name], i);
                    return new RegressionExpectation(
                        parsed.Statistics,
                        parsed.Values.Select(v => v.Value).ToArray(),
                        parsed.Values.Select((v, n) => i.PreserveOrder ? v.Index : n + 1).ToArray(),
                        i.Deduplicate,
                        i.PreserveOrder
                    );
                }
            );
            var fixture = new RecipeRegressionFixture(
                Guid.NewGuid().ToString("N"),
                id,
                entry.WorkspaceId,
                request.Name.Trim(),
                new Dictionary<string, string>(request.ArtificialInputs),
                request.Mode,
                request.Strategy,
                expected,
                rendered.Strategy,
                (request.SqlContains ?? []).ToArray(),
                (request.SqlExcludes ?? []).ToArray(),
                DateTimeOffset.UtcNow,
                SqlHash(rendered)
            );
            var run = EvaluateRegression(entry, fixture);
            if (!run.Success)
                throw new AppException("REGRESSION_ASSERTION_FAILED", string.Join("; ", run.Failures), 422);
            store.Batch([
                new("RecipeRegressionFixtures", fixture.Id, entry.WorkspaceId, fixture),
                new("RecipeRegressionRuns", run.Id, entry.WorkspaceId, run),
            ]);
            return fixture;
        });
    }

    public void DeleteRegression(string recipeId, string fixtureId, int expectedVersion) =>
        store.WithWriteLock(() =>
        {
            var entry = Require(recipeId);
            if (entry.Definition.Version != expectedVersion)
                throw new AppException("RECIPE_VERSION_CONFLICT", "Reload the saved recipe.", 409);
            var fixture = store.Get<RecipeRegressionFixture>("RecipeRegressionFixtures", fixtureId);
            if (fixture is null || fixture.RecipeId != recipeId)
                throw new AppException("REGRESSION_FIXTURE_NOT_FOUND", "Fixture not found.", 404);
            store.Delete("RecipeRegressionFixtures", fixtureId);
            return true;
        });

    public async Task<IReadOnlyList<RecipeRegressionRun>> RunRegressions(string id, CancellationToken ct)
    {
        await Revalidate(id, ct);
        return RegressionRuns(id);
    }

    private IEnumerable<StoredRecord> RegressionRecords(RecipeEntry entry) =>
        store
            .All<RecipeRegressionFixture>("RecipeRegressionFixtures", entry.WorkspaceId)
            .Where(f => f.RecipeId == entry.Definition.Id)
            .Select(f =>
            {
                var run = EvaluateRegression(entry, f);
                return new StoredRecord("RecipeRegressionRuns", run.Id, entry.WorkspaceId, run);
            })
            .ToArray();

    private static string SqlHash(RecipeRenderResult result) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(result.Sql + "\\n" + System.Text.Json.JsonSerializer.Serialize(result.Parameters))
            )
        );

    private RecipeRegressionRun EvaluateRegression(RecipeEntry entry, RecipeRegressionFixture fixture)
    {
        var failures = new List<string>();
        // Portable/catalog data is untrusted. A malformed fixture becomes one failed run,
        // never a failure of the recipe/schema validation transaction.
        if (
            fixture.ArtificialInputs is null
            || fixture.ArtificialInputs.Count > 50
            || fixture.ArtificialInputs.Any(p => p.Value is null)
            || fixture.ArtificialInputs.Values.Sum(v => (long)v.Length) > 65536
            || fixture.Expected is null
            || fixture.Expected.Count > 50
            || fixture.Expected.Any(p =>
                p.Value is null
                || p.Value.Statistics is null
                || p.Value.Values is null
                || p.Value.Ordinals is null
                || p.Value.Values.Count > 100000
                || p.Value.Ordinals.Count > 100000
                || !fixture.ArtificialInputs.ContainsKey(p.Key)
            )
            || fixture.SqlContains is null
            || fixture.SqlExcludes is null
            || fixture.SqlContains.Count + fixture.SqlExcludes.Count > 40
            || fixture.SqlContains.Concat(fixture.SqlExcludes).Any(text => string.IsNullOrEmpty(text) || text.Length > 2000)
            || fixture.ExpectedSqlHash is not { Length: 64 }
            || !fixture.ExpectedSqlHash.All(Uri.IsHexDigit)
        )
            return new(
                Guid.NewGuid().ToString("N"),
                fixture.Id,
                entry.Definition.Id,
                entry.Definition.Version,
                DateTimeOffset.UtcNow,
                false,
                ["Saved fixture data is malformed; recreate the artificial baseline."]
            );
        if (entry.Validation.Status is not ("Valid" or "Warning"))
            failures.Add("Recipe schema validation requires review.");
        try
        {
            var rendered = renderer.Render(entry.Definition, fixture.ArtificialInputs, fixture.Mode, fixture.Strategy);
            if (SqlHash(rendered) != fixture.ExpectedSqlHash)
                failures.Add("Generated SQL or parameter contract changed.");
            if (rendered.Strategy != fixture.ExpectedStrategy)
                failures.Add("Selected SQL strategy changed.");
            foreach (var expected in fixture.Expected)
            {
                var input = entry.Definition.Inputs.FirstOrDefault(i => i.Name == expected.Key);
                if (input is null)
                {
                    failures.Add(expected.Key + ": input removed.");
                    continue;
                }
                if (input.Deduplicate != expected.Value.Deduplicate || input.PreserveOrder != expected.Value.PreserveOrder)
                    failures.Add(expected.Key + ": duplicate or ordering policy changed.");
                var actual = new IdProcessor().Parse(fixture.ArtificialInputs[expected.Key], input);
                if (actual.Statistics != expected.Value.Statistics)
                    failures.Add(expected.Key + ": input count or duplicate statistics changed.");
                if (!actual.Values.Select(v => v.Value).SequenceEqual(expected.Value.Values))
                    failures.Add(expected.Key + ": values or duplicate order changed.");
                if (!actual.Values.Select((v, n) => input.PreserveOrder ? v.Index : n + 1).SequenceEqual(expected.Value.Ordinals))
                    failures.Add(expected.Key + ": ordinals changed.");
            }
            foreach (var text in fixture.SqlContains)
                if (!rendered.Sql.Contains(text, StringComparison.Ordinal))
                    failures.Add("Required SQL property missing: " + text);
            foreach (var text in fixture.SqlExcludes)
                if (rendered.Sql.Contains(text, StringComparison.Ordinal))
                    failures.Add("Forbidden SQL property present: " + text);
            if (rendered.Sql != renderer.Render(entry.Definition, fixture.ArtificialInputs, fixture.Mode, fixture.Strategy).Sql)
                failures.Add("SQL rendering is not deterministic.");
        }
        catch (RecipeEngineException ex)
        {
            failures.Add(ex.Code + ": " + ex.Message);
        }
        return new(
            Guid.NewGuid().ToString("N"),
            fixture.Id,
            entry.Definition.Id,
            entry.Definition.Version,
            DateTimeOffset.UtcNow,
            failures.Count == 0,
            failures
        );
    }
}
