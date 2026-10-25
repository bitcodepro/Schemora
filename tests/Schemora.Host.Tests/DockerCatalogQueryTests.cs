using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class DockerCatalogQueryTests
{
    [Fact]
    public void FixedRichCatalogQueryParsesAndFitsTheProcessArgumentLimit()
    {
        Assert.True(
            DockerCatalogQuery.Text.Length < 24_000,
            "Leave room for escaped process arguments within the Windows command-line limit."
        );
        new TSql170Parser(true).Parse(new StringReader(DockerCatalogQuery.Text), out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Base64TransportPreservesUnicodeQuotesAndWrappingWithOrderedKeys()
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                tables = new[]
                {
                    new
                    {
                        schema = "dbo",
                        name = "Клієнти]",
                        columns = new[]
                        {
                            new
                            {
                                name = "Tenant",
                                dataType = "int",
                                isNullable = false,
                            },
                            new
                            {
                                name = "Key",
                                dataType = "nvarchar(40)",
                                isNullable = true,
                            },
                        },
                        primaryKey = new[] { new { name = "Tenant" }, new { name = "Key" } },
                    },
                },
                relations = new[]
                {
                    new
                    {
                        name = "FK_Клієнти",
                        fromSchema = "dbo",
                        fromTable = "Клієнти]",
                        toSchema = "dbo",
                        toTable = "Клієнти]",
                        columns = new[] { new { source = "Tenant", target = "Tenant" }, new { source = "Key", target = "Key" } },
                    },
                },
            }
        );
        var base64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        var wrapped = "\uFEFF" + string.Join("\r\n", base64.Chunk(37).Select(chunk => new string(chunk))) + "\r\n";
        var graph = DockerCatalogQuery.Parse(wrapped, "Commerce]", "source.dacpac");
        var table = Assert.Single(graph.Tables);
        Assert.Equal("[Commerce]]].[dbo].[Клієнти]]]", table.Id);
        Assert.Equal(new[] { "Tenant", "Key" }, table.PrimaryKey);
        Assert.True(table.Columns[1].IsNullable);
        Assert.Equal("nvarchar(40)", table.Columns[1].DataType);
        var relation = Assert.Single(graph.Relations);
        Assert.Equal(table.Id, relation.FromTableId);
        Assert.Equal(new[] { "Tenant", "Key" }, relation.FromColumns);
        Assert.Equal(new[] { "Tenant", "Key" }, relation.ToColumns);
    }

    [Fact]
    public void MalformedCatalogNeverReturnsAnEmptySuccessfulGraph()
    {
        Assert.Equal(
            "DOCKER_CATALOG_INVALID",
            Assert.Throws<AppException>(() => DockerCatalogQuery.Parse("not-base64", "Db", "fixture")).Code
        );
        var incomplete = Convert.ToBase64String(Encoding.Unicode.GetBytes("{}"));
        Assert.Equal(
            "DOCKER_CATALOG_INVALID",
            Assert.Throws<AppException>(() => DockerCatalogQuery.Parse(incomplete, "Db", "fixture")).Code
        );
    }

    [Fact]
    public void GeneratedDeploymentStreamStopsBeforeExceedingItsBound()
    {
        using var output = new BoundedOutputStream(new MemoryStream(), 4);
        output.Write([1, 2, 3]);
        var error = Assert.Throws<AppException>(() => output.Write([4, 5]));
        Assert.Equal("DOCKER_SCRIPT_TOO_LARGE", error.Code);
        Assert.Equal(3, output.Length);
    }
}
