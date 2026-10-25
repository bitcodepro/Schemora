using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class FailedRebuildTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraFailedRebuild-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FailedRebuildKeepsTheExistingModelAndReportsTheError()
    {
        var source = Path.Combine(directory, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "Main.sqlproj"),
            "<Project><Sdk Name=\"Microsoft.Build.Sql\" Version=\"2.3.0\"/><PropertyGroup><Name>Main</Name><DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP></PropertyGroup></Project>"
        );
        // The view references a missing table: static analysis still yields a partial model, while the real build fails.
        File.WriteAllText(
            Path.Combine(source, "Schema.sql"),
            "CREATE TABLE dbo.Items(Id int NOT NULL, CONSTRAINT PK_Items PRIMARY KEY(Id));\nGO\nCREATE VIEW dbo.Broken AS SELECT Id FROM dbo.Missing;"
        );
        var store = new CatalogStore(Path.Combine(directory, "data", "catalog.sqlite"));
        var workspace = new Workspace("ws", "failed rebuild", [source], [], DateTimeOffset.UtcNow);
        store.Put("Workspaces", workspace.Id, workspace.Id, workspace);
        var reader = new DacpacReader();
        var scanner = new WorkspaceScanner(
            store,
            new WorkspaceDiscovery(),
            reader,
            new StaticSqlReader(),
            new SqlSourceClassifier(),
            new SqlProjectBuilder(new Toolchain(Path.GetDirectoryName(store.Path)!), reader)
        );

        await scanner.Scan(workspace, "full", null, false, (_, _) => { }, default);
        var before = store.Catalog(workspace.Id).Databases.Single();
        Assert.Equal("partial", before.Status);

        var outcome = await scanner.Scan(workspace, "rebuild", null, true, (_, _) => { }, default);
        var after = store.Catalog(workspace.Id).Databases.Single();

        Assert.True(outcome.Partial);
        Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.StartsWith("Main: ", StringComparison.Ordinal));
        Assert.Equal("partial", after.Status);
        Assert.Equal(before.CurrentSnapshotId, after.CurrentSnapshotId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // MSBuild worker processes can hold files for a few seconds after the build; retry rather than leave the folder behind.
        for (var attempt = 1; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 40)
            {
                Thread.Sleep(250);
            }
        }
    }
}
