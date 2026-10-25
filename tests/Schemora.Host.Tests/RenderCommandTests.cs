using System.Text.Json;
using Schemora.Host;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class RenderCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraRender-" + Guid.NewGuid().ToString("N"));

    public RenderCommandTests()
    {
        Directory.CreateDirectory(directory);
        // The same envelope the app's Export JSON button writes.
        File.WriteAllText(
            Path.Combine(directory, "recipe.json"),
            """
            {
              "formatVersion": 1,
              "recipe": {
                "id": "r1", "name": "Orders in a period", "description": "", "tags": [], "dialect": "sqlserver",
                "databaseIds": ["db"], "version": 3, "compatibilityLevel": 130,
                "sqlTemplate": "SELECT o.OrderId FROM dbo.Orders AS o WHERE o.CreatedAt >= {{value:From}} AND o.CustomerId IN ({{ids:Ids}});",
                "inputs": [
                  { "name": "From", "type": "date", "deduplicate": false, "preserveOrder": true, "strategy": "auto" },
                  { "name": "Ids", "type": "int", "deduplicate": false, "preserveOrder": true, "strategy": "list" }
                ]
              },
              "databaseReferences": [{ "id": "db", "name": "Shop" }]
            }
            """
        );
        File.WriteAllText(Path.Combine(directory, "ids.txt"), "7\n8\n");
    }

    private (int Code, string Output, string Error) Render(params string[] arguments)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = RenderCommand.Run(arguments, output, error, directory);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void RendersStandaloneSqlFromAnExportedRecipeAndAnInputFile()
    {
        var (code, output, error) = Render("--recipe", "recipe.json", "--input", "From=2026-10-01", "--input", "Ids=@ids.txt");
        Assert.Equal(0, code);
        Assert.Equal(
            "SELECT o.OrderId FROM dbo.Orders AS o WHERE o.CreatedAt >= CONVERT(date, N'2026-10-01', 23) AND o.CustomerId IN (7, 8);\n",
            output
        );
        Assert.Equal("", error);
    }

    [Fact]
    public void ParameterizedModeWritesJsonWithTheParameterToAFile()
    {
        var (code, output, _) = Render(
            "--recipe",
            "recipe.json",
            "--input",
            "From=2026-10-01",
            "--input",
            "Ids=7",
            "--mode",
            "parameterized",
            "--out",
            "out.json"
        );
        Assert.Equal(0, code);
        Assert.Equal("", output);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "out.json")));
        Assert.Contains("OPENJSON(@SchemoraInputs", result.RootElement.GetProperty("sql").GetString(), StringComparison.Ordinal);
        Assert.Equal("@SchemoraInputs", result.RootElement.GetProperty("parameters")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void InvalidValuesFailWithTheEngineCodeAndRowDiagnostics()
    {
        var (code, output, error) = Render("--recipe", "recipe.json", "--input", "From=01.10.2026", "--input", "Ids=7");
        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.StartsWith("ID_VALIDATION_FAILED:", error, StringComparison.Ordinal);
        Assert.Contains("DATE_INVALID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageErrorsAreReportedWithoutRendering()
    {
        Assert.Equal(1, Render("--input", "Ids=7").Code);
        Assert.Equal(1, Render("--recipe", "recipe.json", "--bogus", "x").Code);
        Assert.Equal(1, Render("--recipe", "recipe.json", "--mode", "parameterized", "--format", "sql").Code);
        Assert.Equal(2, Render("--recipe", "missing.json").Code);
        Assert.Contains("Usage:", Render("--help").Output, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void SeveralRecipesRenderIntoOneScriptWithABatchEachAndSharedInputs()
    {
        File.WriteAllText(
            Path.Combine(directory, "customers.json"),
            """
            {
              "id": "r2", "name": "Customers", "description": "", "tags": [], "dialect": "sqlserver",
              "databaseIds": ["db"], "version": 1, "compatibilityLevel": 130,
              "sqlTemplate": "SELECT c.Name FROM dbo.Customers AS c JOIN {{table:Ids}} AS i ON i.Value = c.CustomerId;",
              "inputs": [{ "name": "Ids", "type": "int", "deduplicate": false, "preserveOrder": true, "strategy": "temp" }]
            }
            """
        );
        var (code, output, error) = Render(
            "--recipe",
            "recipe.json",
            "--recipe",
            "customers.json",
            "--input",
            "From=2026-10-01",
            "--input",
            "Ids=@ids.txt"
        );
        Assert.Equal(0, code);
        Assert.StartsWith("-- Schemora: 2 recipes rendered together.", output, StringComparison.Ordinal);
        Assert.Contains("-- 1/2: Orders in a period (v3)\n", output, StringComparison.Ordinal);
        Assert.Contains("-- 2/2: Customers (v1)\n", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split("\nGO\n").Length - 1);
        Assert.Contains("CREATE TABLE [#schemora_Ids]", output, StringComparison.Ordinal);
        Assert.Contains("warning: Customers: Ids:", error, StringComparison.Ordinal);
        var unknown = Render("--recipe", "recipe.json", "--recipe", "customers.json", "--input", "Other=1");
        Assert.Equal(2, unknown.Code);
        Assert.Contains("INPUT_UNKNOWN", unknown.Error, StringComparison.Ordinal);
    }
}
