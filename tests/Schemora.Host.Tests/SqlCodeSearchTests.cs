using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class SqlCodeSearchTests
{
    [Fact]
    public void SearchUsesCurrentDefinitionsReturnsBoundedExcerptAndExactSourceLineAndTreatsWildcardsLiterally()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sql-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            store.Put("Workspaces", "ws", "ws", new Workspace("ws", "demo", [], [], DateTimeOffset.UtcNow));
            var definition = "CREATE PROCEDURE dbo.Lookup AS\n-- comment\nSELECT N'Київ' AS City;\n" + new string(' ', 2000);
            SchemaGraph Graph(string body) =>
                new(
                    "demo",
                    "source.sql",
                    DateTimeOffset.UtcNow,
                    [],
                    [],
                    [],
                    [new("p", "dbo", "Lookup", "procedure", [], Definition: body, SourcePath: "source.sql", Line: 10)]
                );
            store.Put(
                "SchemaSnapshots",
                "old",
                "ws",
                new SnapshotRecord("old", "ws", "db", DateTimeOffset.UtcNow, "static", "old", Graph("obsolete_marker"))
            );
            store.Put(
                "SchemaSnapshots",
                "new",
                "ws",
                new SnapshotRecord("new", "ws", "db", DateTimeOffset.UtcNow, "static", "new", Graph(definition))
            );
            store.Put("Databases", "db", "ws", new DatabaseInfo("db", "ws", "demo", "source.sql", "sql", "ready", "new", null));
            JsonElement Search(string q) =>
                JsonSerializer.SerializeToElement(WorkspaceSearch.Read(store, "ws", q, "sqlCode", 0, 50), CatalogStore.Json);
            var result = Search("київ");
            Assert.Equal(1, result.GetProperty("total").GetInt32());
            var hit = result.GetProperty("items")[0];
            Assert.Equal(12, hit.GetProperty("line").GetInt32());
            Assert.Equal(3, hit.GetProperty("definitionLine").GetInt32());
            Assert.Contains("Київ", hit.GetProperty("detail").GetString());
            Assert.True(hit.GetProperty("detail").GetString()!.Length <= 300);
            Assert.Equal(0, Search("obsolete_marker").GetProperty("total").GetInt32());
            Assert.Equal(0, Search("%").GetProperty("total").GetInt32());
            Assert.Equal(0, Search("' OR 1=1 --").GetProperty("total").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
