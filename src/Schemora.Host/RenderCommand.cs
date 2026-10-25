using System.Text;
using System.Text.Json;
using Schemora.RecipeEngine;

namespace Schemora.Host;

/// <summary>
/// `Schemora.Host.exe render`: renders SQL from an exported recipe file without starting the host or opening the catalog,
/// so it runs beside a live instance and in scripts. Input values and generated SQL are written only where the caller asks.
/// </summary>
internal static class RenderCommand
{
    private const string Usage = """
        Usage: Schemora.Host.exe render --recipe <recipe.json> [--recipe <other.json>]...
                                        [--input Name=values | --input Name=@file]...
                                        [--mode standalone|parameterized] [--strategy auto|values|temp|openjson|list]
                                        [--format sql|json] [--out <file>]

          --recipe    A recipe exported from Schemora (Export JSON) or a bare recipe definition. Repeat it to render
                      several recipes into one script: each becomes its own batch (GO), and every recipe takes the
                      inputs whose names it uses.
          --input     Values for one input, like the ID box in the app; @file reads them from a text file.
          --format    sql prints the script (default for standalone); json prints SQL, parameters, statistics and warnings
                      (default for parameterized, which needs the JSON parameter), as an array for several recipes.
          --out       Writes the result to a file instead of standard output.

        Warnings go to standard error. Exit codes: 0 rendered, 1 usage error, 2 recipe or input error.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error, string? workingDirectory = null)
    {
        var directory = workingDirectory ?? Environment.CurrentDirectory;
        var recipePaths = new List<string>();
        string? mode = null,
            strategy = null,
            format = null,
            outPath = null;
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                var name = arguments[index];
                if (name is "--help" or "-h" or "/?")
                {
                    output.WriteLine(Usage);
                    return 0;
                }
                if (index + 1 >= arguments.Count)
                    return Fail(error, 1, "CLI_ARGUMENT_MISSING", $"Missing value for {name}.");
                var value = arguments[++index];
                switch (name)
                {
                    case "--recipe":
                        recipePaths.Add(value);
                        break;
                    case "--mode":
                        mode = value;
                        break;
                    case "--strategy":
                        strategy = value;
                        break;
                    case "--format":
                        format = value;
                        break;
                    case "--out":
                        outPath = value;
                        break;
                    case "--input":
                        var separator = value.IndexOf('=');
                        if (separator <= 0)
                            return Fail(error, 1, "CLI_INPUT_INVALID", "Use --input Name=values or --input Name=@file.");
                        var inputName = value[..separator];
                        var values = value[(separator + 1)..];
                        if (inputs.ContainsKey(inputName))
                            return Fail(error, 1, "CLI_INPUT_DUPLICATE", $"Input {inputName} was given more than once.");
                        inputs[inputName] = values.StartsWith('@') ? File.ReadAllText(Path.Combine(directory, values[1..])) : values;
                        break;
                    default:
                        return Fail(error, 1, "CLI_ARGUMENT_UNKNOWN", $"Unknown option {name}. Use --help.");
                }
            }
            if (recipePaths.Count == 0)
                return Fail(error, 1, "CLI_RECIPE_REQUIRED", "Pass --recipe <recipe.json>. Use --help for the full syntax.");
            if (format is not (null or "sql" or "json"))
                return Fail(error, 1, "CLI_FORMAT_INVALID", "Use --format sql or --format json.");
            var recipes = recipePaths.Select(path => ReadRecipe(File.ReadAllText(Path.Combine(directory, path)))).ToArray();
            mode ??= "standalone";
            format ??= mode == "parameterized" ? "json" : "sql";
            if (format == "sql" && mode == "parameterized")
                return Fail(error, 1, "CLI_FORMAT_INVALID", "Parameterized SQL needs its JSON parameter; use --format json.");
            string text;
            if (recipes.Length == 1)
            {
                var result = new SqlRecipeRenderer().Render(recipes[0], inputs, mode, strategy ?? "auto");
                foreach (var warning in result.Warnings)
                    error.WriteLine("warning: " + warning);
                text = format == "sql" ? result.Sql : JsonSerializer.Serialize(result, Json) + "\n";
            }
            else
            {
                var used = recipes.SelectMany(recipe => recipe.Inputs).Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
                if (inputs.Keys.FirstOrDefault(key => !used.Contains(key)) is { } unknown)
                    return Fail(error, 2, "INPUT_UNKNOWN", $"Input {unknown} is not used by any of the recipes.");
                var results = new List<(RecipeDefinition Recipe, RecipeRenderResult Result)>();
                foreach (var recipe in recipes)
                {
                    var names = recipe.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
                    try
                    {
                        results.Add(
                            (
                                recipe,
                                new SqlRecipeRenderer().Render(
                                    recipe,
                                    inputs.Where(pair => names.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal),
                                    mode,
                                    strategy ?? "auto"
                                )
                            )
                        );
                    }
                    catch (RecipeEngineException)
                    {
                        error.WriteLine("recipe " + recipe.Name + ":");
                        throw;
                    }
                }
                foreach (var (recipe, result) in results)
                foreach (var warning in result.Warnings)
                    error.WriteLine("warning: " + recipe.Name + ": " + warning);
                text =
                    format == "sql"
                        ? RecipeScripts.Combine(results.Select(item => (item.Recipe.Name, item.Recipe.Version, item.Result.Sql)).ToArray())
                        : JsonSerializer.Serialize(
                            results.Select(item => new
                            {
                                Recipe = item.Recipe.Name,
                                item.Recipe.Version,
                                Result = item.Result,
                            }),
                            Json
                        ) + "\n";
            }
            if (outPath is null)
                output.Write(text);
            else
                File.WriteAllText(Path.Combine(directory, outPath), text, new UTF8Encoding(false));
            return 0;
        }
        catch (RecipeEngineException failure)
        {
            Fail(error, 2, failure.Code, failure.Message);
            foreach (var issue in failure.Errors.Take(20))
                error.WriteLine($"  line {issue.Line}, column {issue.Column}: {issue.Code} {issue.Message} [{issue.RawValue}]");
            return 2;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Fail(error, 2, "CLI_FILE_INVALID", failure.Message);
        }
    }

    private static RecipeDefinition ReadRecipe(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var definition = root.TryGetProperty("recipe", out var envelope) ? envelope : root;
        return definition.Deserialize<RecipeDefinition>(Json)
            ?? throw new JsonException("The recipe file does not contain a recipe definition.");
    }

    private static int Fail(TextWriter error, int code, string errorCode, string message)
    {
        error.WriteLine(errorCode + ": " + message);
        return code;
    }
}
