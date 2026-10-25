using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace Schemora.SqlServer.Tests;

public sealed class MigrationDraftTests
{
    private static SchemaColumn Col(string name, string type = "int", bool nullable = false) => new(name, type, nullable);

    private static SchemaGraph Model(string database, bool next)
    {
        var customers = new SchemaTable(
            database + ".customers",
            "dbo",
            "Customers",
            next
                ? [Col("Id"), Col("Name", "nvarchar(200)"), Col("Email", "nvarchar(320)", true)]
                : [Col("Id"), Col("Name", "nvarchar(100)", true), Col("Fax", "varchar(20)", true)],
            ["Id"],
            next ? [new SchemaUniqueKey(database + ".uq", "UQ_Customers_Email", ["Email"])] : []
        );
        var orders = new SchemaTable(database + ".orders", "dbo", "Orders", [Col("Id"), Col("CustomerId")], ["Id"]);
        var tables = new List<SchemaTable> { customers, orders };
        tables.Add(
            next
                ? new SchemaTable(
                    database + ".invoices",
                    "sales",
                    "Invoices",
                    [Col("Id"), Col("OrderId"), Col("Total", "decimal(18,2)")],
                    ["Id"]
                )
                : new SchemaTable(database + ".legacy", "dbo", "Legacy]Log", [Col("Id")], [])
        );
        var relations = new List<SchemaRelation>
        {
            new(
                "fk1",
                next ? "FK_Orders_Customers" : "FK__Orders__Custome__3B75D760",
                orders.Id,
                customers.Id,
                ["CustomerId"],
                ["Id"],
                "declared-fk",
                1
            ),
        };
        if (next)
            relations.Add(new("fk2", "FK_Invoices_Orders", database + ".invoices", orders.Id, ["OrderId"], ["Id"], "declared-fk", 1));
        var indexes = new List<SchemaIndex>
        {
            new(
                database + ".pk",
                customers.Id,
                next ? "PK_Customers" : "PK__Customer__3214EC07A1B2C3D4",
                "clustered",
                true,
                true,
                [new("Id", false)],
                []
            ),
            new(
                database + ".ix",
                orders.Id,
                "IX_Orders_Customer",
                "nonclustered",
                false,
                false,
                [new("CustomerId", next)],
                next ? ["Id"] : []
            ),
        };
        if (next)
            indexes.Add(new(database + ".uqi", customers.Id, "UQ_Customers_Email", "nonclustered", true, false, [new("Email", false)], []));
        return new(database, "fixture", DateTimeOffset.UnixEpoch, tables, relations, [], [], indexes);
    }

    [Fact]
    public void TheDraftScriptsEveryStructuralChangeAndCommentsOutDataLoss()
    {
        var draft = MigrationDraft.Create(Model("Production", false), Model("Repository", true));
        var sql = draft.Sql;
        Assert.StartsWith("-- Schemora migration draft. Review only: Schemora never runs it.", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [sales].[Invoices] (\n    [Id] int NOT NULL,", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [dbo].[Customers] ADD [Email] nvarchar(320) NULL;", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [dbo].[Customers] ALTER COLUMN [Name] nvarchar(200) NOT NULL;", sql, StringComparison.Ordinal);
        Assert.Contains("becomes NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("-- ALTER TABLE [dbo].[Customers] DROP COLUMN [Fax];", sql, StringComparison.Ordinal);
        Assert.Contains("-- DROP TABLE [dbo].[Legacy]]Log];", sql, StringComparison.Ordinal);
        // The base FK has a system name, so it is dropped by that name and re-added under the repository's name.
        Assert.Contains("ALTER TABLE [dbo].[Orders] DROP CONSTRAINT [FK__Orders__Custome__3B75D760];", sql, StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE [dbo].[Orders] WITH CHECK ADD CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers] ([Id]);",
            sql,
            StringComparison.Ordinal
        );
        Assert.Contains("DROP INDEX [IX_Orders_Customer] ON [dbo].[Orders];", sql, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE NONCLUSTERED INDEX [IX_Orders_Customer] ON [dbo].[Orders] ([CustomerId] DESC) INCLUDE ([Id]);",
            sql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ALTER TABLE [dbo].[Customers] ADD CONSTRAINT [UQ_Customers_Email] UNIQUE NONCLUSTERED ([Email]);",
            sql,
            StringComparison.Ordinal
        );
        // The primary key only changed its system name, which is not a change worth scripting.
        Assert.DoesNotContain("PRIMARY KEY ([Id]);", sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf("DROP CONSTRAINT [FK__", StringComparison.Ordinal) < sql.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        Assert.True(
            sql.IndexOf("CREATE TABLE", StringComparison.Ordinal) < sql.IndexOf("FOREIGN KEY ([OrderId])", StringComparison.Ordinal)
        );
        Assert.Equal(2, draft.CommentedOut);
        var parsed = new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void MatchingModelsProduceNoStatements()
    {
        var draft = MigrationDraft.Create(Model("A", true), Model("B", true));
        Assert.Contains("-- The models match; there is nothing to change.", draft.Sql, StringComparison.Ordinal);
        Assert.Equal(0, draft.Statements);
    }
}
