using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemora.RecipeEngine;

public sealed class SqlRecipeRenderer
{
    private const int MaximumScriptBytes = 64 * 1024 * 1024;

    // Long literal IN lists hurt plan compilation and can hit SQL Server's expression limits; larger inputs use a rowset.
    public const int MaximumListValues = 1000;

    public RecipeRenderResult Render(
        RecipeDefinition recipe,
        IReadOnlyDictionary<string, string> inputText,
        string mode = "standalone",
        string strategy = "auto"
    )
    {
        // Inputs used as {{value:...}} take exactly one value and render as a typed literal (or a JSON lookup when parameterized).
        var valueInputs = RecipeTemplate
            .ValidateDefinition(recipe)
            .Where(token => token.Kind == "value")
            .Select(token => token.InputName)
            .ToHashSet(StringComparer.Ordinal);
        if (mode is not ("standalone" or "parameterized"))
            throw new RecipeEngineException("RENDER_MODE_INVALID", "Choose standalone or parameterized mode.");
        if (strategy is not ("auto" or "values" or "temp" or "openjson" or "list"))
            throw new RecipeEngineException("RENDER_STRATEGY_INVALID", "Choose auto, values, temp, openjson or list strategy.");
        if (inputText.Values.Sum(value => (long)(value?.Length ?? 0)) > IdProcessor.MaximumCharacters)
            throw new RecipeEngineException("ID_INPUT_TOO_LARGE", "Combined recipe input exceeds 8 Mi characters.");
        var knownNames = recipe.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        if (inputText.Keys.Any(key => !knownNames.Contains(key)))
            throw new RecipeEngineException("INPUT_UNKNOWN", "Values were supplied for an undefined recipe input.");
        var parsed = new Dictionary<string, IdParseResult>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var input in recipe.Inputs)
        {
            var optional = input.Optional && valueInputs.Contains(input.Name);
            if (!inputText.TryGetValue(input.Name, out var text))
                text = optional ? "" : throw new RecipeEngineException("INPUT_REQUIRED", $"Input {input.Name} was not supplied.");
            var values = new IdProcessor().Parse(text, input);
            if (values.Statistics.Invalid != 0)
                throw new RecipeEngineException(
                    "ID_VALIDATION_FAILED",
                    $"Input {input.Name} contains {values.Statistics.Invalid} invalid values; no SQL was generated.",
                    values.Errors.Select(error => error with { Message = input.Name + ": " + error.Message }).ToArray()
                );
            if (values.Statistics.OutputCount == 0 && !(optional && values.Statistics.Total == 0))
                throw new RecipeEngineException("ID_VALUES_REQUIRED", $"Input {input.Name} needs at least one valid value.");
            if (optional && values.Statistics.Total == 0)
                warnings.Add($"{input.Name}: no value was entered, so the optional input is NULL.");
            if (valueInputs.Contains(input.Name) && values.Statistics.OutputCount > 1)
                throw new RecipeEngineException(
                    "VALUE_INPUT_SINGLE",
                    $"Input {input.Name} expects exactly one value; {values.Statistics.OutputCount} were supplied."
                );
            if (input.Collation?.EndsWith("_UTF8", StringComparison.OrdinalIgnoreCase) == true && recipe.CompatibilityLevel < 150)
                throw new RecipeEngineException(
                    "UTF8_COMPATIBILITY_REQUIRED",
                    "UTF-8 VARCHAR collations require SQL Server 2019 or later (compatibility level 150+ in this renderer)."
                );
            if (values.Statistics.Duplicates > 0)
                warnings.Add(
                    input.Deduplicate
                        ? $"{input.Name}: duplicate removal was explicitly enabled; {values.Statistics.OutputCount} values remain."
                        : $"{input.Name}: all {values.Statistics.Duplicates} duplicate occurrences were retained."
                );
            if (input.PreserveOrder && !valueInputs.Contains(input.Name))
                warnings.Add(
                    $"{input.Name}: original positions are exposed as [Ordinal]. IN does not order query results; use a table placeholder and ORDER BY its alias.[Ordinal] when result order matters."
                );
            if (InputRules.Type(input) is "varchar" or "nvarchar" && input.Collation is null)
                warnings.Add(
                    $"{input.Name}: input rowsets use DATABASE_DEFAULT collation; select the intended target database before executing the copied script."
                );
            if (values.Statistics.OutputCount >= 10_000)
                warnings.Add(
                    $"{input.Name}: {values.Statistics.OutputCount} values may require reviewing indexes and the SQL execution plan."
                );
            parsed.Add(input.Name, values);
        }
        if (parsed.Values.Sum(value => (long)value.Statistics.Total) > IdProcessor.MaximumValues)
            throw new RecipeEngineException("ID_COUNT_LIMIT", "Combined recipe input exceeds 100000 values.");
        var selected = recipe.Inputs.ToDictionary(
            input => input.Name,
            input =>
                mode == "parameterized" ? "openjson"
                : valueInputs.Contains(input.Name) ? "scalar"
                : strategy != "auto" ? strategy
                : input.Strategy != "auto" ? input.Strategy
                : parsed[input.Name].Statistics.OutputCount <= 100 ? "values"
                : "temp",
            StringComparer.Ordinal
        );
        foreach (var input in recipe.Inputs.Where(input => selected[input.Name] == "list"))
        {
            if (parsed[input.Name].Statistics.OutputCount > MaximumListValues)
                throw new RecipeEngineException(
                    "LIST_STRATEGY_LIMIT",
                    $"The IN list strategy is limited to {MaximumListValues} values; {input.Name} has {parsed[input.Name].Statistics.OutputCount}. Use auto, values or temp."
                );
            warnings.RemoveAll(warning => warning.StartsWith(input.Name + ": original positions", StringComparison.Ordinal));
        }
        var needsJson = selected.Values.Contains("openjson", StringComparer.Ordinal);
        if (needsJson && recipe.CompatibilityLevel < 130)
            throw new RecipeEngineException(
                "OPENJSON_COMPATIBILITY_REQUIRED",
                "OPENJSON requires SQL Server 2016+ and database compatibility level 130+. Use standalone values/temp for older targets."
            );
        var payload = needsJson
            ? JsonSerializer.Serialize(
                recipe
                    .Inputs.Where(input => selected[input.Name] == "openjson")
                    .ToDictionary(
                        input => input.Name,
                        input =>
                            parsed[input.Name]
                                .Values.Select(
                                    (value, index) =>
                                        new
                                        {
                                            ordinal = input.PreserveOrder ? value.Index : index + 1,
                                            value = InputRules.Canonical(input, value.Value),
                                        }
                                )
                                .ToArray(),
                        StringComparer.Ordinal
                    )
            )
            : "";
        var before = new StringBuilder();
        var after = new StringBuilder();
        var datasets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in recipe.Inputs)
        {
            var values = parsed[input.Name].Values;
            switch (selected[input.Name])
            {
                // A list input still needs a rowset when the template also uses it as {{table:...}}.
                case "values" or "list":
                    datasets.Add(
                        input.Name,
                        "(SELECT [v].[Ordinal], [v].[Value] FROM (VALUES\n"
                            + string.Join(
                                ",\n",
                                values.Select(
                                    (value, index) => "    (" + Ordinal(input, value, index) + ", " + Scalar(input, value.Value) + ")"
                                )
                            )
                            + "\n) AS [v]([Ordinal], [Value]))"
                    );
                    break;
                case "temp":
                    var name = InputRules.Quote(RecipeTemplate.InputTableName(input.Name));
                    before
                        .Append("IF OBJECT_ID(N'tempdb..")
                        .Append(RecipeTemplate.InputTableName(input.Name))
                        .Append("') IS NOT NULL DROP TABLE ")
                        .Append(name)
                        .Append(";\n")
                        .Append("CREATE TABLE ")
                        .Append(name)
                        .Append(" ([Ordinal] int NOT NULL, [Value] ")
                        .Append(InputRules.SqlType(input))
                        .Append(InputRules.Collate(input))
                        .Append(" NOT NULL);\n");
                    for (var offset = 0; offset < values.Count; offset += 1000)
                    {
                        before.Append("INSERT INTO ").Append(name).Append(" ([Ordinal], [Value]) VALUES\n");
                        before
                            .Append(
                                string.Join(
                                    ",\n",
                                    values
                                        .Skip(offset)
                                        .Take(1000)
                                        .Select(
                                            (value, index) =>
                                                "    (" + Ordinal(input, value, offset + index) + ", " + Scalar(input, value.Value) + ")"
                                        )
                                )
                            )
                            .Append(";\n");
                        CheckSize(before.Length);
                    }
                    after.Append("\nDROP TABLE ").Append(name).Append(";\n");
                    datasets.Add(input.Name, name);
                    break;
                case "openjson":
                    var conversion =
                        InputRules.Type(input) == "varchar"
                            ? "CONVERT("
                                + InputRules.SqlType(input)
                                + ", [j].[Value]"
                                + InputRules.Collate(input)
                                + ")"
                                + InputRules.Collate(input)
                            : "CONVERT("
                                + InputRules.SqlType(input)
                                + ", [j].[Value]"
                                + InputRules.ConvertStyle(input)
                                + ")"
                                + InputRules.Collate(input);
                    datasets.Add(
                        input.Name,
                        "(SELECT [j].[Ordinal], "
                            + conversion
                            + " AS [Value] FROM OPENJSON(@SchemoraInputs, '$."
                            + input.Name
                            + "') WITH ([Ordinal] int '$.ordinal', [Value] nvarchar(max) '$.value') AS [j])"
                    );
                    break;
            }
        }
        if (needsJson && mode == "standalone")
        {
            before.Insert(0, "DECLARE @SchemoraInputs nvarchar(max) = CONVERT(nvarchar(max), N'');\n");
            var declaration = new StringBuilder();
            for (var offset = 0; offset < payload.Length; offset += 4000)
                declaration
                    .Append("SET @SchemoraInputs += ")
                    .Append(TextLiteral(payload.Substring(offset, Math.Min(4000, payload.Length - offset))))
                    .Append(";\n");
            before.Insert("DECLARE @SchemoraInputs nvarchar(max) = CONVERT(nvarchar(max), N'');\n".Length, declaration);
        }
        var inputs = recipe.Inputs.ToDictionary(input => input.Name, StringComparer.Ordinal);
        var body = RecipeTemplate.Replace(
            recipe,
            token =>
                token.Kind == "value"
                    ? selected[token.InputName] == "scalar"
                        ? parsed[token.InputName].Values.Count == 0
                            ? "CAST(NULL AS " + InputRules.SqlType(inputs[token.InputName]) + ")"
                            : Scalar(inputs[token.InputName], parsed[token.InputName].Values[0].Value)
                        : "(SELECT [v].[Value] FROM " + datasets[token.InputName] + " AS [v])"
                    : token.Kind != "ids"
                        ? datasets[token.InputName]
                        : selected[token.InputName] == "list"
                            ? string.Join(
                                ", ",
                                parsed[token.InputName].Values.Select(value => ListLiteral(inputs[token.InputName], value.Value))
                            )
                            : "SELECT [i].[Value] FROM " + datasets[token.InputName] + " AS [i]"
        );
        var sql = before + body + "\n" + after;
        CheckSize(sql.Length);
        if (Encoding.UTF8.GetByteCount(sql) > MaximumScriptBytes)
            throw new RecipeEngineException("SQL_OUTPUT_TOO_LARGE", "Generated SQL exceeds 64 MiB.");
        using (var text = new StringReader(sql))
        {
            var script = new TSql170Parser(true).Parse(text, out var errors) as TSqlScript;
            if (errors.Count != 0)
                throw new RecipeEngineException(
                    "SQL_RENDER_PARSE_FAILED",
                    "The rendered SQL did not parse. Check the recipe template and placeholder position; no SQL was returned."
                );
            if (script?.Batches.Count > 1)
                throw new RecipeEngineException("SQL_BATCHES_UNSUPPORTED", "Recipes use one SQL command batch; remove GO separators.");
        }
        var parameters =
            mode == "parameterized" && needsJson ? new[] { new SqlBoundParameter("@SchemoraInputs", "nvarchar(max)", payload) } : [];
        var example = mode == "parameterized" ? Example(sql, parameters) : null;
        if (example is not null)
            CheckSize(example.Length);
        var strategies = selected.Values.Where(value => value != "scalar").Distinct(StringComparer.Ordinal).ToArray();
        return new(
            sql,
            mode,
            strategies.Length == 0 ? (valueInputs.Count != 0 ? "scalar" : "values")
                : strategies.Length == 1 ? strategies[0]
                : "mixed",
            parsed.ToDictionary(pair => pair.Key, pair => pair.Value.Statistics, StringComparer.Ordinal),
            warnings,
            parameters,
            example,
            mode == "parameterized" ? DapperExample(sql, parameters) : null
        );
    }

    private static string Ordinal(RecipeInputDefinition input, ProcessedId value, int outputIndex) =>
        (input.PreserveOrder ? value.Index : outputIndex + 1).ToString(CultureInfo.InvariantCulture);

    private static string Scalar(RecipeInputDefinition input, string value)
    {
        var type = InputRules.Type(input);
        var literal = type switch
        {
            "int" or "bigint" or "bit" or "decimal" => InputRules.Canonical(input, value),
            "varchar" => TextLiteral(value) + InputRules.Collate(input),
            _ => TextLiteral(InputRules.Canonical(input, value)),
        };
        return "CONVERT(" + InputRules.SqlType(input) + ", " + literal + InputRules.ConvertStyle(input) + ")" + InputRules.Collate(input);
    }

    // Integer IDs read best as plain literals; other types keep the typed CONVERT used by the rowset strategies.
    private static string ListLiteral(RecipeInputDefinition input, string value) =>
        InputRules.Type(input) is "int" or "bigint"
            ? long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : Scalar(input, value);

    private static string TextLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Example(string sql, IReadOnlyList<SqlBoundParameter> parameters)
    {
        var code = new StringBuilder(
            "using Microsoft.Data.SqlClient;\nusing System.Data;\n\nawait using var connection = new SqlConnection(\"YOUR_CONNECTION_STRING\");\nawait connection.OpenAsync();\nawait using var command = connection.CreateCommand();\ncommand.CommandText = "
        )
            .Append(JsonSerializer.Serialize(sql))
            .Append(";\n");
        foreach (var parameter in parameters)
            code.Append("command.Parameters.Add(")
                .Append(JsonSerializer.Serialize(parameter.Name))
                .Append(", SqlDbType.NVarChar, -1).Value = ")
                .Append(JsonSerializer.Serialize(parameter.Value))
                .Append(";\n");
        return code.Append(
                "// This is a copyable example; Schemora does not execute it.\nawait using var reader = await command.ExecuteReaderAsync();\n"
            )
            .ToString();
    }

    private static string DapperExample(string sql, IReadOnlyList<SqlBoundParameter> parameters)
    {
        var code = new StringBuilder(
            "using Dapper;\nusing Microsoft.Data.SqlClient;\nusing System.Data;\n\nawait using var connection = new SqlConnection(\"YOUR_CONNECTION_STRING\");\nawait connection.OpenAsync();\nvar parameters = new DynamicParameters();\n"
        );
        foreach (var p in parameters)
            code.Append("parameters.Add(")
                .Append(JsonSerializer.Serialize(p.Name))
                .Append(", ")
                .Append(JsonSerializer.Serialize(p.Value))
                .Append(", DbType.String, size: -1);\n");
        code.Append("var sql = ")
            .Append(JsonSerializer.Serialize(sql))
            .Append(
                ";\n// Runs only when you execute the copied code in your own application.\nvar rows = await connection.QueryAsync(sql, parameters);\n"
            );
        CheckSize(code.Length);
        return code.ToString();
    }

    private static void CheckSize(int characters)
    {
        if (characters > MaximumScriptBytes / 2)
            throw new RecipeEngineException("SQL_OUTPUT_TOO_LARGE", "Generated SQL/example exceeds the 64 MiB UTF-16 memory limit.");
    }
}
