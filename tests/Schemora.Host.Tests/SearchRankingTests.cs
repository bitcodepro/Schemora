using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class SearchRankingTests
{
    [Fact]
    public void ExactNamesComeFirstFiltersApplyAndANewSnapshotRefreshesTheIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "search-rank-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(Path.Combine(directory, "catalog.sqlite"));
            store.Put("Workspaces", "ws", "ws", new Workspace("ws", "demo", [], [], DateTimeOffset.UtcNow));
            static SchemaTable Table(string id, string name) => new(id, "dbo", name, [new("Id", "int", false)], ["Id"]);
            void Snapshot(string id, string database, params SchemaTable[] tables) =>
                store.Put(
                    "SchemaSnapshots",
                    id,
                    "ws",
                    new SnapshotRecord(
                        id,
                        "ws",
                        database,
                        DateTimeOffset.UtcNow,
                        "static",
                        id,
                        new(database, "", DateTimeOffset.UtcNow, tables, [], [])
                    )
                );
            void Current(string database, string snapshot) =>
                store.Put("Databases", database, "ws", new DatabaseInfo(database, "ws", database, "x.sql", "sql", "ready", snapshot, null));
            Snapshot("s1", "sales", Table("a", "OrderLines"), Table("b", "Orders"), Table("c", "CustomerOrders"));
            Snapshot("s2", "archive", Table("d", "Orders"));
            Current("sales", "s1");
            Current("archive", "s2");
            JsonElement Search(string q, string? kind = null, string? database = null) =>
                JsonSerializer.SerializeToElement(WorkspaceSearch.Read(store, "ws", q, kind, 0, 50, database), CatalogStore.Json);
            string[] Names(JsonElement result) =>
                result.GetProperty("items").EnumerateArray().Select(hit => hit.GetProperty("name").GetString()!).ToArray();

            var tables = Search("orders", "table");
            // Exact names first, then prefixes, then other matches.
            Assert.Equal(["dbo.Orders", "dbo.Orders", "dbo.CustomerOrders"], Names(tables));
            Assert.Equal(3, Search("orders").GetProperty("kinds").GetProperty("table").GetInt32());
            Assert.Equal(["dbo.Orders"], Names(Search("orders", "table", "archive")));

            Snapshot("s3", "archive", Table("e", "Invoices"));
            Current("archive", "s3");
            Assert.Equal(["dbo.Invoices"], Names(Search("invoices", "table")));
            Assert.Equal(2, Search("orders", "table").GetProperty("total").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
