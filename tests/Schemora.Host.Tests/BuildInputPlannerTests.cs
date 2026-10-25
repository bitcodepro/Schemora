using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class BuildInputPlannerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraBuildInputs-" + Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly string project;
    private readonly SqlProjectBuilder builder;

    public BuildInputPlannerTests()
    {
        var repo = FindRepository();
        source = Path.Combine(directory, "source");
        Copy(Path.Combine(repo, "tests", "Fixtures", "ComplexBuild"), source);
        project = Path.Combine(source, "Main", "Main.sqlproj");
        using (var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions()))
        {
            model.AddObjects(File.ReadAllText(Path.Combine(source, "references", "External.sql")));
            DacPackageExtensions.BuildPackage(
                Path.Combine(source, "references", "External.dacpac"),
                model,
                new PackageMetadata { Name = "ExternalDatabase", Version = "1.0" }
            );
        }
        builder = new(new Toolchain(Path.Combine(directory, "data")), new DacpacReader());
    }

    [Fact]
    public async Task LiteralClosureIncludesSiblingProjectsImportsDacpacSqlcmdAndHiddenInputsWithoutValues()
    {
        File.WriteAllText(Path.Combine(source, "Main", ".gitignore"), "Hidden.sql\n");
        var hidden = Path.Combine(source, "Main", "Hidden.sql");
        File.WriteAllText(hidden, "CREATE TABLE dbo.Hidden(Id int);");
        var manifest = await builder.Plan(project);
        Assert.True(manifest.Complete, JsonSerializer.Serialize(manifest.Diagnostics));
        Assert.Equal(2, manifest.Projects.Count);
        foreach (
            var suffix in new[]
            {
                "Shared/Shared.sqlproj",
                "build/shared.props",
                "build/shared.targets",
                "references/External.dacpac",
                "deploy/Initial.sql",
                "Main/Hidden.sql",
            }
        )
            Assert.Contains(manifest.Inputs, input => input.Path.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal));
        Assert.DoesNotContain("SYNTHETIC_SQLCMD_VALUE_f26c", JsonSerializer.Serialize(manifest));
        Assert.All(manifest.Inputs, input => Assert.Equal(64, input.Sha256.Length));
        Assert.True((await builder.CheckManifest(manifest)).Current);
        File.WriteAllText(Path.Combine(source, "Shared", "New.sql"), "CREATE TABLE dbo.NewShared(Id int);");
        Assert.False((await builder.CheckManifest(manifest)).Current);
    }

    [Fact]
    public async Task OptionalImportAdditionRemovalAndMutationInvalidateFullClosure()
    {
        var props = Path.Combine(source, "build", "shared.props");
        File.WriteAllText(props, "<Project><Import Project=\"optional.props\" Condition=\"Exists('optional.props')\" /></Project>");
        var first = await builder.Plan(project);
        Assert.True(first.Complete, JsonSerializer.Serialize(first.Diagnostics));
        var optional = Path.Combine(source, "build", "optional.props");
        File.WriteAllText(optional, "<Project><PropertyGroup><SyntheticFlag>one</SyntheticFlag></PropertyGroup></Project>");
        Assert.False((await builder.CheckManifest(first)).Current);
        var second = await builder.Plan(project);
        Assert.True(second.Complete, JsonSerializer.Serialize(second.Diagnostics));
        File.WriteAllText(optional, "<Project><PropertyGroup><SyntheticFlag>two</SyntheticFlag></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(optional, second.Inputs.Single(i => i.Path == optional).LastWrite.UtcDateTime);
        Assert.False((await builder.CheckManifest(second)).Current); // equal length/stamp still rehashes
        var third = await builder.Plan(project);
        File.Delete(optional);
        Assert.False((await builder.CheckManifest(third)).Current);
    }

    [Fact]
    public async Task DynamicImportsAndUndeclaredCustomTaskInputsAreExplicitlyIncomplete()
    {
        var props = Path.Combine(source, "build", "main.props");
        File.WriteAllText(props, "<Project><Import Project=\"$([System.IO.Path]::Combine('a','b.props'))\" /></Project>");
        var manifest = await builder.Plan(project);
        Assert.False(manifest.Complete);
        Assert.Contains(manifest.Diagnostics, d => d.Code == "BUILD_DYNAMIC_INPUT_UNRESOLVED");
        var error = await Assert.ThrowsAsync<AppException>(() => builder.Build(project, true, default));
        Assert.Equal("BUILD_INPUTS_INCOMPLETE", error.Code);
        File.WriteAllText(props, "<Project><Target Name=\"Unknown\"><Exec Command=\"echo not executed\" /></Target></Project>");
        manifest = await builder.Plan(project);
        Assert.False(manifest.Complete);
        Assert.Contains(manifest.Diagnostics, d => d.Code == "BUILD_CUSTOM_TASK_INPUTS_UNVERIFIED");
    }

    [Fact]
    public async Task TrustedComplexBuildUsesStagedClosurePreservesOriginalsAndPersistsVerifiedManifest()
    {
        var before = State(source);
        SqlProjectBuildResult result;
        try
        {
            result = await builder.BuildWithManifest(project, true, default);
        }
        catch (AppException error)
        {
            throw new InvalidOperationException(error.Code + "\n" + error.Detail, error);
        }
        Assert.Contains(result.Graph.Tables, t => t.Name == "MainItems");
        Assert.True(result.Manifest.Complete);
        Assert.Equal(before, State(source));
        var stored = builder.ReadManifest(result.Manifest.Id);
        Assert.NotNull(stored);
        Assert.Equal(result.Manifest.Fingerprint, stored.Fingerprint);
        Assert.True((await builder.CheckManifest(stored)).Current);
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "data", "builds")));
        File.WriteAllText(Path.Combine(source, "Shared", "Added.sql"), "CREATE TABLE dbo.Added(Id int NOT NULL);");
        Assert.False((await builder.CheckManifest(stored)).Current);
        Assert.Equal("PROJECT_TRUST_REQUIRED", (await Assert.ThrowsAsync<AppException>(() => builder.Build(project, false, default))).Code);
    }

    [Fact]
    public async Task ScannerReusesOnlyVerifiedClosureAndInvalidatesSiblingGrowthOutsideWorkspace()
    {
        var store = new CatalogStore(Path.Combine(directory, "scan-catalog.sqlite"));
        var workspace = new Workspace("ws", "Complex", [Path.GetDirectoryName(project)!], [], DateTimeOffset.UtcNow);
        store.Put("Workspaces", "ws", "ws", workspace);
        var scanner = new WorkspaceScanner(
            store,
            new Schemora.Workspaces.Discovery.WorkspaceDiscovery(),
            new DacpacReader(),
            new StaticSqlReader(),
            new SqlSourceClassifier(),
            builder
        );
        await scanner.Scan(workspace, "rebuild", null, true, (_, _) => { }, default);
        var before = store.All<DatabaseInfo>("Databases", "ws").Single();
        var snapshot = store.Get<SnapshotRecord>("SchemaSnapshots", before.CurrentSnapshotId!)!;
        Assert.NotNull(snapshot.BuildManifestId);
        Assert.NotNull(builder.ReadManifest(snapshot.BuildManifestId));
        Assert.Equal(0, (await scanner.Scan(workspace, "quick", null, false, (_, _) => { }, default)).DatabasesUpdated);
        Assert.Equal(snapshot.Id, store.All<DatabaseInfo>("Databases", "ws").Single().CurrentSnapshotId);
        File.WriteAllText(Path.Combine(source, "Shared", "NewSiblingInput.sql"), "CREATE TABLE dbo.SiblingGrowth(Id int);");
        var refresh = await scanner.Scan(workspace, "quick", null, false, (_, _) => { }, default);
        Assert.Equal(1, refresh.DatabasesUpdated);
        var after = store.All<DatabaseInfo>("Databases", "ws").Single();
        Assert.NotEqual(snapshot.Id, after.CurrentSnapshotId);
        Assert.Equal("partial", after.Status);
        Assert.Null(store.Get<SnapshotRecord>("SchemaSnapshots", after.CurrentSnapshotId!)!.BuildManifestId);
        Assert.Contains(refresh.Diagnostics, d => d.Contains("authoritative model reuse was refused", StringComparison.Ordinal));
    }

    private static string State(string path) =>
        string.Join(
            "\n",
            Directory
                .GetFiles(path, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => file + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
        );

    private static string FindRepository()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Schemora.slnx")))
                return current.FullName;
        throw new InvalidOperationException("Repository fixtures were not found.");
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var child in Directory.GetDirectories(from))
            Copy(child, Path.Combine(to, Path.GetFileName(child)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
