using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Schemora.RecipeEngine;

// Synthetic data only. No SQL, parameter payload or credentials are written to disk or logs.
var port = Environment.GetEnvironmentVariable("SCHEMORA_SQL_PORT") ?? throw new InvalidOperationException("SQL port unavailable.");
var password =
    Environment.GetEnvironmentVariable("SQLCMDPASSWORD") ?? throw new InvalidOperationException("Ephemeral SQL credential unavailable.");
var builder = new SqlConnectionStringBuilder
{
    DataSource = "tcp:127.0.0.1," + port,
    UserID = "sa",
    Password = password,
    InitialCatalog = "master",
    Encrypt = true,
    TrustServerCertificate = true,
    ConnectTimeout = 10,
};
await using var connection = new SqlConnection(builder.ConnectionString);
await connection.OpenAsync();
await using (var setup = connection.CreateCommand())
{
    setup.CommandText = "CREATE DATABASE SchemoraGeneratedSql; ALTER DATABASE SchemoraGeneratedSql SET COMPATIBILITY_LEVEL=160;";
    await setup.ExecuteNonQueryAsync();
}
await connection.ChangeDatabaseAsync("SchemoraGeneratedSql");
var cases = new List<object>();
await Run(
    "10000-temp",
    new("Ids", "int"),
    string.Join(',', Enumerable.Range(1, 10000)),
    Enumerable.Range(1, 10000).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray(),
    "standalone",
    "temp"
);
await Run(
    "10000-one-json-parameter",
    new("Ids", "bigint"),
    string.Join(',', Enumerable.Range(1, 10000)),
    Enumerable.Range(1, 10000).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray(),
    "parameterized",
    "auto"
);
await Run(
    "quoted-unicode-values",
    new("Ids", "nvarchar", 100),
    "N'O''Brien',N'Україна',\"x'); DROP TABLE dbo.Victims;--\",\"  padded  \",N'😀'",
    ["O'Brien", "Україна", "x'); DROP TABLE dbo.Victims;--", "  padded  ", "😀"],
    "standalone",
    "values"
);
await Run(
    "unicode-json-standalone",
    new("Ids", "nvarchar", 100),
    "N'Київ',N'O''Brien',N'😀'",
    ["Київ", "O'Brien", "😀"],
    "standalone",
    "openjson"
);
await Run(
    "cp1251-values",
    new("Ids", "varchar", 20, "windows-1251", Collation: "Cyrillic_General_100_CI_AS"),
    "Київ,Привіт",
    ["Київ", "Привіт"],
    "standalone",
    "values"
);
await Run(
    "cp1251-temp",
    new("Ids", "varchar", 20, "windows-1251", Collation: "Cyrillic_General_100_CI_AS"),
    "Київ,Привіт",
    ["Київ", "Привіт"],
    "standalone",
    "temp"
);
await Run(
    "cp1251-json",
    new("Ids", "varchar", 20, "windows-1251", Collation: "Cyrillic_General_100_CI_AS"),
    "Київ,Привіт",
    ["Київ", "Привіт"],
    "parameterized",
    "auto"
);
await Run(
    "utf8-values",
    new("Ids", "varchar", 30, "utf-8", Collation: "Latin1_General_100_BIN2_UTF8"),
    "Київ,Привіт,😀",
    ["Київ", "Привіт", "😀"],
    "standalone",
    "values"
);
await Run(
    "utf8-temp",
    new("Ids", "varchar", 30, "utf-8", Collation: "Latin1_General_100_BIN2_UTF8"),
    "Київ,Привіт,😀",
    ["Київ", "Привіт", "😀"],
    "standalone",
    "temp"
);
await Run(
    "utf8-json",
    new("Ids", "varchar", 30, "utf-8", Collation: "Latin1_General_100_BIN2_UTF8"),
    "Київ,Привіт,😀",
    ["Київ", "Привіт", "😀"],
    "parameterized",
    "auto"
);

// The IN list strategy is filtered against the same rowset, so every value must match its own literal.
const string listTemplate =
    "SET NOCOUNT ON; SELECT x.[Ordinal], x.[Value] FROM {{table:Ids}} AS x WHERE x.[Value] IN ({{ids:Ids}}) ORDER BY x.[Ordinal];";
await Run(
    "int-list",
    new("Ids", "int"),
    string.Join(',', Enumerable.Range(1, 1000)),
    Enumerable.Range(1, 1000).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray(),
    "standalone",
    "list",
    listTemplate
);
await Run(
    "quoted-unicode-list",
    new("Ids", "nvarchar", 100),
    "N'O''Brien',N'Україна',N'😀'",
    ["O'Brien", "Україна", "😀"],
    "standalone",
    "list",
    listTemplate
);
await Run(
    "cp1251-list",
    new("Ids", "varchar", 20, "windows-1251", Collation: "Cyrillic_General_100_CI_AS"),
    "Київ,Привіт",
    ["Київ", "Привіт"],
    "standalone",
    "list",
    listTemplate
);

// Single values must not depend on the session language or DATEFORMAT (German implies dmy).
string ValueCheck(string expected) =>
    "SET NOCOUNT ON; SET LANGUAGE German; SELECT 1 AS [Ordinal], CASE WHEN {{value:Ids}} = "
    + expected
    + " THEN N'ok' ELSE N'bad' END AS [Value];";
await Run("date-value", new("Ids", "date"), "2026-10-01", ["ok"], "standalone", "auto", ValueCheck("DATEFROMPARTS(2026, 10, 1)"));
await Run("date-value-json", new("Ids", "date"), "2026-10-01", ["ok"], "parameterized", "auto", ValueCheck("DATEFROMPARTS(2026, 10, 1)"));
await Run(
    "datetime2-value",
    new("Ids", "datetime2"),
    "2026-11-01T08:30",
    ["ok"],
    "standalone",
    "auto",
    ValueCheck("DATETIME2FROMPARTS(2026, 11, 1, 8, 30, 0, 0, 7)")
);
await Run(
    "datetime2-value-json",
    new("Ids", "datetime2"),
    "2026-11-01T08:30",
    ["ok"],
    "parameterized",
    "auto",
    ValueCheck("DATETIME2FROMPARTS(2026, 11, 1, 8, 30, 0, 0, 7)")
);
await Run("bit-value", new("Ids", "bit"), "false", ["ok"], "standalone", "auto", ValueCheck("CAST(0 AS bit)"));
await Run(
    "decimal-values",
    new("Ids", "decimal", Precision: 18, Scale: 4),
    "1500.5,-0.25",
    ["1500.5000", "-0.2500"],
    "standalone",
    "values"
);
await Run(
    "decimal-json",
    new("Ids", "decimal", Precision: 18, Scale: 4),
    "1500.5,-0.25",
    ["1500.5000", "-0.2500"],
    "parameterized",
    "auto"
);
await Run(
    "decimal-value",
    new("Ids", "decimal", Precision: 10, Scale: 2),
    "1500.50",
    ["ok"],
    "standalone",
    "auto",
    ValueCheck("CAST(1500.5 AS decimal(10,2))")
);
Console.WriteLine(
    JsonSerializer.Serialize(
        new
        {
            success = true,
            cases,
            generatedSqlPersisted = false,
            runtimeCredentialsPersisted = false,
        }
    )
);

async Task Run(
    string name,
    RecipeInputDefinition input,
    string text,
    string[] expected,
    string mode,
    string strategy,
    string? template = null
)
{
    var recipe = new RecipeDefinition(
        "synthetic",
        name,
        "Synthetic SQL Server execution check",
        [],
        "sqlserver",
        ["fixture"],
        1,
        template ?? "SET NOCOUNT ON; SELECT x.[Ordinal], x.[Value] FROM {{table:Ids}} AS x ORDER BY x.[Ordinal];",
        [input],
        CompatibilityLevel: 160
    );
    var render = new SqlRecipeRenderer().Render(recipe, new Dictionary<string, string> { ["Ids"] = text }, mode, strategy);
    await using var command = connection.CreateCommand();
    command.CommandTimeout = 60;
    command.CommandText = render.Sql;
    foreach (var parameter in render.Parameters)
        command.Parameters.Add(parameter.Name, SqlDbType.NVarChar, -1).Value = parameter.Value;
    var actual = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        if (reader.GetInt32(0) != actual.Count + 1)
            throw new InvalidOperationException(name + ": ordinal mismatch.");
        actual.Add(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!);
    }
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        throw new InvalidOperationException(
            name + ": exact row/value mismatch (expected " + expected.Length + ", actual " + actual.Count + ")."
        );
    cases.Add(
        new
        {
            name,
            rows = actual.Count,
            mode,
            strategy = render.Strategy,
            parameters = render.Parameters.Count,
            exactValues = true,
        }
    );
}
