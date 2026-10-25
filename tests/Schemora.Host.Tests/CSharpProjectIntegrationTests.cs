using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class CSharpProjectIntegrationTests
{
    [Fact]
    public async Task SelectedConfigurationAndSourceContentChangeGraphWithoutRescan()
    {
        using var f = new Fixture();
        f.Settings(new("project", "Debug", "net10.0"));
        var debug = await f.Service.GetSnapshot("ws", default);
        Assert.DoesNotContain(debug.Graph.Relations, e => e.Evidence.Any(v => v.Kind == "dapper-sql"));
        f.Settings(new("project", "Release", "net10.0"));
        var release = await f.Service.GetSnapshot("ws", default);
        var first = Assert.Single(release.Graph.Relations, e => e.Evidence.Any(v => v.Kind == "dapper-sql"));
        Assert.Equal("b", first.Relation.ToTableId);
        Assert.False(first.IsTrusted);
        Assert.NotEqual(debug.Revision, release.Revision);
        Assert.Contains(release.Graph.Diagnostics, d => d.Code == "CSHARP_PROJECT_PARTIAL");
        File.WriteAllText(f.Source, File.ReadAllText(f.Source).Replace("dbo.Beta b", "dbo.Gamma b"));
        var changed = await f.Service.GetSnapshot("ws", default);
        Assert.Equal("c", Assert.Single(changed.Graph.Relations, e => e.Evidence.Any(v => v.Kind == "dapper-sql")).Relation.ToTableId);
        Assert.NotEqual(release.Revision, changed.Revision);
    }

    [Fact]
    public async Task TrustedCacheInvalidatesOnImportedContentAndGraphReadsDoNotRunMSBuild()
    {
        using var f = new Fixture();
        var settings = new CSharpProjectSettings("project", "Release", "net10.0", true);
        f.Settings(settings);
        var baseline = new CSharpProjectContextEvaluator().Evaluate(new(f.Project, "Release", "net10.0"));
        var imported = Path.Combine(f.Directory, "trusted-only.props");
        File.WriteAllText(imported, "<Project />");
        var cached = baseline with
        {
            Fingerprint = "trusted-output",
            ProjectInputs = (baseline.ProjectInputs ?? []).Append(imported).ToArray(),
        };
        f.Store.Put(
            "UserSettings",
            CSharpProjectEndpoints.Key("ws", "project") + ":trusted",
            "ws",
            new CSharpTrustedContextCache(baseline.Fingerprint, CSharpProjectEndpoints.InputFingerprint(cached), cached)
        );
        Assert.Equal("trusted-output", CSharpProjectEndpoints.ReadContext(f.Store, "ws", f.Project, settings).Fingerprint);
        var before = await f.Service.GetSnapshot("ws", default);
        File.WriteAllText(imported, "<Project><PropertyGroup><Changed>true</Changed></PropertyGroup></Project>");
        Assert.Contains(
            CSharpProjectEndpoints.ReadContext(f.Store, "ws", f.Project, settings).Diagnostics,
            d => d.Code == "CSHARP_TRUST_REFRESH_REQUIRED"
        );
        var after = await f.Service.GetSnapshot("ws", default);
        Assert.NotEqual(before.Revision, after.Revision);
        Assert.Contains(after.Graph.Diagnostics, d => d.Code == "CSHARP_TRUST_REFRESH_REQUIRED");
        // This SDK cannot be evaluated by MSBuild. Safe graph inspection still returns a model.
        File.WriteAllText(f.Project, File.ReadAllText(f.Project).Replace("Microsoft.NET.Sdk", "Schemora.Intentionally.Unavailable.Sdk"));
        Assert.Contains((await f.Service.Get("ws", default)).Diagnostics, d => d.Code == "CSHARP_TRUST_REFRESH_REQUIRED");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Schemora-CSharpContext-" + Guid.NewGuid().ToString("N"));
        public string Project { get; }
        public string Source { get; }
        public CatalogStore Store { get; }
        public RelationshipService Service { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "code"));
            Project = Path.Combine(Directory, "code", "Repo.csproj");
            Source = Path.Combine(Directory, "code", "Repo.cs");
            File.WriteAllText(
                Project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><PropertyGroup Condition=\"'$(Configuration)|$(Platform)' == 'Release|AnyCPU'\"><DefineConstants>$(DefineConstants);RELEASE_QUERY</DefineConstants></PropertyGroup></Project>"
            );
            File.WriteAllText(
                Source,
                "class Repo { void Read(dynamic connection) {\n#if RELEASE_QUERY\nconnection.Query(\"SELECT a.Id,b.Id FROM dbo.Alpha a JOIN dbo.Beta b ON a.Pointer=b.Id\");\n#endif\n} }"
            );
            Store = new(Path.Combine(Directory, "catalog.sqlite"));
            Store.Put(
                "Workspaces",
                "ws",
                null,
                new Workspace("ws", "CSharp context", [Path.GetDirectoryName(Project)!], [], DateTimeOffset.UtcNow)
            );
            var graph = new SchemaGraph(
                "Commerce",
                "synthetic",
                DateTimeOffset.UtcNow,
                [
                    new("a", "dbo", "Alpha", [new("Id", "int", false), new("Pointer", "int", false)], ["Id"]),
                    new("b", "dbo", "Beta", [new("Id", "int", false)], ["Id"]),
                    new("c", "dbo", "Gamma", [new("Id", "int", false)], ["Id"]),
                ],
                [],
                []
            );
            Store.Put(
                "SchemaSnapshots",
                "snapshot",
                "ws",
                new SnapshotRecord("snapshot", "ws", "db", DateTimeOffset.UtcNow, "static", "snapshot", graph)
            );
            Store.Put(
                "Databases",
                "db",
                "ws",
                new DatabaseInfo("db", "ws", "Commerce", "synthetic", "sql", "ready", "snapshot", DateTimeOffset.UtcNow)
            );
            Store.Put(
                "Sources",
                "project",
                "ws",
                new DiscoveredSource("project", Project, "csharpProject", "frozen", DateTimeOffset.UtcNow, [])
            );
            Store.Put("Sources", "code", "ws", new DiscoveredSource("code", Source, "csharp", "frozen", DateTimeOffset.UtcNow, []));
            Service = new(Store, new RelationshipAnalyzer());
        }

        public void Settings(CSharpProjectSettings value) =>
            Store.Put("UserSettings", CSharpProjectEndpoints.Key("ws", "project"), "ws", value);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, true);
        }
    }
}
