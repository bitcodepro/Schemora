using System.Text.Json;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class SchemaCompareTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraCompare-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogStore store;
    private readonly ChangeService changes;

    public SchemaCompareTests()
    {
        Directory.CreateDirectory(directory);
        store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
        // Only the comparison collaborators are needed; repair-related services stay unused here.
        changes = new ChangeService(store, null!, null!, null!, new SchemaDiffer(), null!, new DacpacReader());
        Snapshot("repo", "ws", "db-repo", Table("repo", [new("OrderId", "int", false), new("Total", "decimal(10,2)", false)]));
        Snapshot(
            "stage",
            "ws",
            "db-stage",
            Table("stage", [new("OrderId", "int", false), new("Total", "decimal(12,2)", true), new("Note", "nvarchar(100)", true)])
        );
        Snapshot("other", "ws-other", "db-other", Table("other", [new("OrderId", "int", false)]));
    }

    private static SchemaTable Table(string database, IReadOnlyList<SchemaColumn> columns) =>
        new(database + ":orders", "dbo", "Orders", columns, ["OrderId"]);

    private void Snapshot(string id, string workspace, string database, SchemaTable table) =>
        store.Put(
            "SchemaSnapshots",
            id,
            workspace,
            new SnapshotRecord(
                id,
                workspace,
                database,
                DateTimeOffset.UtcNow,
                "full",
                "f",
                new(database, "", DateTimeOffset.UtcNow, [table], [], [])
            )
        );

    private static string[] Kinds(object result) =>
        JsonSerializer
            .SerializeToElement(result)
            .GetProperty("Changes")
            .EnumerateArray()
            .Select(change => change.GetProperty("Kind").GetString() + ":" + change.GetProperty("Column").GetString())
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void ComparesTwoDatabasesOfOneWorkspaceByTableAndColumnName()
    {
        var kinds = Kinds(changes.CompareSnapshots("ws", "repo", "stage"));
        Assert.Equal(["columnAdded:Note", "columnNullabilityChanged:Total", "columnTypeChanged:Total"], kinds);
        var draft = JsonSerializer
            .SerializeToElement(changes.CompareSnapshots("ws", "repo", "stage"))
            .GetProperty("MigrationDraft")
            .GetString();
        Assert.Contains("ALTER TABLE [dbo].[Orders] ADD [Note] nvarchar(100) NULL;", draft, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [dbo].[Orders] ALTER COLUMN [Total] decimal(12,2) NULL;", draft, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesSnapshotsFromAnotherWorkspace()
    {
        Assert.Equal("SNAPSHOT_NOT_FOUND", Assert.Throws<AppException>(() => changes.CompareSnapshots("ws", "repo", "other")).Code);
        Assert.Equal("SNAPSHOT_NOT_FOUND", Assert.Throws<AppException>(() => changes.CompareWithDacpac("ws", "other", "x.dacpac")).Code);
    }

    [Fact]
    public void ComparesASnapshotWithADacpacFileWithoutAddingIt()
    {
        var dacpac = Path.Combine(directory, "stage.dacpac");
        using (var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions()))
        {
            model.AddObjects(
                "CREATE TABLE dbo.Orders(OrderId int NOT NULL PRIMARY KEY, Total decimal(10,2) NOT NULL, Status nvarchar(20) NULL);"
            );
            DacPackageExtensions.BuildPackage(dacpac, model, new PackageMetadata { Name = "Stage" });
        }
        var kinds = Kinds(changes.CompareWithDacpac("ws", "repo", dacpac));
        // The DACPAC also reports its primary key index, which the hand-made snapshot has no record of.
        Assert.Contains("columnAdded:Status", kinds);
        Assert.DoesNotContain(
            kinds,
            kind => kind.StartsWith("table", StringComparison.Ordinal) || kind.StartsWith("columnType", StringComparison.Ordinal)
        );
        Assert.Empty(store.All<DatabaseInfo>("Databases", "ws"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
