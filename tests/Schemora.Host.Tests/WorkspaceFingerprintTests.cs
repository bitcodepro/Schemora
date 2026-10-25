using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class WorkspaceFingerprintTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraFingerprintTests-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogStore store;
    private readonly Workspace workspace;
    private readonly WorkspaceScanner scanner;
    private readonly SqlProjectBuilder builder;

    public WorkspaceFingerprintTests()
    {
        var root = Path.Combine(directory, "checkout", "workspace");
        Directory.CreateDirectory(Path.Combine(root, "db"));
        File.WriteAllText(
            Path.Combine(root, "db", "Commerce.sqlproj"),
            "<Project Sdk=\"Microsoft.Build.Sql/2.1.0\"><PropertyGroup><Name>Commerce</Name></PropertyGroup></Project>"
        );
        File.WriteAllText(
            Path.Combine(root, "db", "Orders.sql"),
            "CREATE TABLE dbo.Orders(Id bigint NOT NULL PRIMARY KEY, InvoiceId int NULL);"
        );
        var data = Path.Combine(directory, "data");
        store = new(Path.Combine(data, "catalog.sqlite"));
        workspace = new("ws", "Fingerprint", [root], [], DateTimeOffset.UtcNow);
        store.Put("Workspaces", workspace.Id, null, workspace);
        var reader = new DacpacReader();
        builder = new(new Toolchain(data), reader);
        scanner = new(store, new WorkspaceDiscovery(), reader, new StaticSqlReader(), new SqlSourceClassifier(), builder);
    }

    [Fact]
    public async Task InheritedAndSharedBuildChangesInvalidateAnAuthoritativeSnapshotBeforeReuse()
    {
        var inherited = Path.Combine(directory, "checkout", "Directory.Build.props");
        var global = Path.Combine(directory, "checkout", "global.json");
        const string secret = "PRIVATE_BUILD_VALUE_e71db";
        File.WriteAllText(inherited, "<Project><PropertyGroup><Synthetic>" + secret + "</Synthetic></PropertyGroup></Project>");
        File.WriteAllText(global, "{\"sdk\":{\"version\":\"10.0.402\"}}");
        var first = await Scan("full");
        Assert.Equal(1, first.DatabasesUpdated);
        var database = store.All<DatabaseInfo>("Databases", "ws").Single();
        var prior = store.Get<SnapshotRecord>("SchemaSnapshots", database.CurrentSnapshotId!)!;
        // Model an immutable authoritative Rebuild result without executing the project.
        var provenance = await builder.Plan(database.SourcePath);
        var manifests = Path.Combine(directory, "data", "build-manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, provenance.Id + ".json"), JsonSerializer.Serialize(provenance, CatalogStore.Json));
        var authoritative = prior with
        {
            Id = "authoritative",
            Mode = "rebuild",
            BuildManifestId = provenance.Id,
            Graph = prior.Graph with { Diagnostics = [] },
        };
        store.Batch([
            new("SchemaSnapshots", authoritative.Id, "ws", authoritative),
            new("Databases", database.Id, "ws", database with { CurrentSnapshotId = authoritative.Id, Status = "ready" }),
        ]);
        Assert.Equal(0, (await Scan("quick")).DatabasesUpdated);
        Assert.Equal("authoritative", store.All<DatabaseInfo>("Databases", "ws").Single().CurrentSnapshotId);
        Assert.Contains(store.All<DiscoveredSource>("Sources", "ws"), s => s.Path == inherited && s.Kind == "buildConfig");
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(store.All<DiscoveredSource>("Sources", "ws"), CatalogStore.Json));

        var old = File.GetLastWriteTimeUtc(inherited);
        File.WriteAllText(inherited, "<Project><PropertyGroup><Changed>true</Changed></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(inherited, old.AddSeconds(2));
        Assert.Equal(1, (await Scan("quick")).DatabasesUpdated);
        var current = store.All<DatabaseInfo>("Databases", "ws").Single();
        Assert.NotEqual("authoritative", current.CurrentSnapshotId);
        Assert.Equal("partial", current.Status);
        Assert.Contains(
            store.Get<SnapshotRecord>("SchemaSnapshots", current.CurrentSnapshotId!)!.Graph.Diagnostics,
            d => d.Code == "BUILD_REQUIRED_FOR_AUTHORITATIVE_MODEL"
        );
        Assert.Equal(0, (await Scan("quick")).DatabasesUpdated);
        File.Delete(global);
        Assert.Equal(1, (await Scan("quick")).DatabasesUpdated);

        // A shared import in another discovered folder is conservatively part of every SQL project.
        var shared = Path.Combine(workspace.Roots[0], "shared", "common.targets");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, "<Project />");
        Assert.Equal(1, (await Scan("quick")).DatabasesUpdated);
        Assert.Equal(0, (await Scan("quick")).DatabasesUpdated);
    }

    [Fact]
    public async Task LockedInheritedConfigurationRetainsLastKnownCatalogAndSnapshot()
    {
        var path = Path.Combine(directory, "checkout", "Directory.Build.targets");
        File.WriteAllText(path, "<Project />");
        await Scan("full");
        var before = store.All<DatabaseInfo>("Databases", "ws").Single();
        var sourceCount = store.All<DiscoveredSource>("Sources", "ws").Count;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<AppException>(() => Scan("full"));
            Assert.Equal("DISCOVERY_INCOMPLETE", error.Code);
        }
        Assert.Equal(before.CurrentSnapshotId, store.All<DatabaseInfo>("Databases", "ws").Single().CurrentSnapshotId);
        Assert.Equal(sourceCount, store.All<DiscoveredSource>("Sources", "ws").Count);
        Assert.Single(store.All<SnapshotRecord>("SchemaSnapshots", "ws"));
    }

    private Task<ScanOutcome> Scan(string mode) => scanner.Scan(workspace, mode, null, false, (_, _) => { }, default);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
