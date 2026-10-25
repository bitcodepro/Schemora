using System.Text;

namespace Schemora.RecipeEngine;

/// <summary>Only ids/name, table/name and value/name substitutions exist; no expressions, evaluation or user-provided replacement code.</summary>
public static class RecipeTemplate
{
    public static IReadOnlyList<RecipeToken> ValidateDefinition(RecipeDefinition recipe)
    {
        if (recipe.SqlTemplate is null || recipe.SqlTemplate.Length > 1024 * 1024)
            throw new RecipeEngineException("TEMPLATE_SIZE_INVALID", "SQL templates must be at most 1 Mi characters.");
        if (!string.Equals(recipe.Dialect, "sqlserver", StringComparison.OrdinalIgnoreCase))
            throw new RecipeEngineException("DIALECT_UNSUPPORTED", "Only the sqlserver dialect is supported.");
        if (recipe.Inputs.Count > 32)
            throw new RecipeEngineException("INPUT_DEFINITION_LIMIT", "A recipe supports at most 32 typed inputs.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in recipe.Inputs)
        {
            InputRules.Validate(input);
            if (!names.Add(input.Name))
                throw new RecipeEngineException(
                    "INPUT_NAME_DUPLICATE",
                    "Input names must be unique, including SQL Server case-insensitive identifier semantics."
                );
        }
        var known = recipe.Inputs.ToDictionary(input => input.Name, StringComparer.Ordinal);
        var text = recipe.SqlTemplate;
        var result = new List<RecipeToken>();
        var state = State.Normal;
        var blockDepth = 0;
        var line = 1;
        var column = 1;
        for (var index = 0; index < text.Length; )
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (current == '{' && next == '{')
            {
                if (state != State.Normal)
                    throw new RecipeEngineException(
                        "PLACEHOLDER_CONTEXT_INVALID",
                        "Recipe placeholders must be SQL syntax, outside comments, quoted identifiers and string literals."
                    );
                var close = text.IndexOf("}}", index + 2, StringComparison.Ordinal);
                if (close < 0)
                    throw new RecipeEngineException("PLACEHOLDER_UNCLOSED", "A recipe placeholder has no closing }}.");
                var body = text[(index + 2)..close];
                var parts = body.Split(':');
                if (parts.Length != 2 || parts[0] is not ("ids" or "table" or "value") || !InputRules.IsName(parts[1]))
                    throw new RecipeEngineException(
                        "PLACEHOLDER_UNKNOWN",
                        "Only {{ids:InputName}}, {{table:InputName}} and {{value:InputName}} are supported."
                    );
                if (!known.ContainsKey(parts[1]))
                    throw new RecipeEngineException(
                        "PLACEHOLDER_INPUT_UNKNOWN",
                        $"Placeholder input {parts[1]} is not defined (names are case-sensitive)."
                    );
                var length = close + 2 - index;
                result.Add(new(parts[0], parts[1], index, length, line, column));
                for (var step = 0; step < length; step++)
                    Advance(text[index++]);
                continue;
            }
            if (state == State.Normal && current == '}' && next == '}')
                throw new RecipeEngineException("PLACEHOLDER_UNKNOWN", "Unexpected closing recipe placeholder.");
            if (
                state == State.Normal
                && text.AsSpan(index).StartsWith("@SchemoraInputs", StringComparison.OrdinalIgnoreCase)
                && (index + 14 >= text.Length || !char.IsLetterOrDigit(text[index + 14]) && text[index + 14] != '_')
            )
                throw new RecipeEngineException(
                    "TEMPLATE_RESERVED_PARAMETER",
                    "@SchemoraInputs is reserved for the generated parameterized example."
                );
            if (state == State.Normal)
            {
                if (current == '-' && next == '-')
                {
                    state = State.LineComment;
                    Step(2);
                    continue;
                }
                if (current == '/' && next == '*')
                {
                    state = State.BlockComment;
                    blockDepth = 1;
                    Step(2);
                    continue;
                }
                if (current == '\'')
                    state = State.SingleQuoted;
                else if (current == '"')
                    state = State.DoubleQuoted;
                else if (current == '[')
                    state = State.Bracketed;
            }
            else if (state == State.LineComment && current == '\n')
                state = State.Normal;
            else if (state == State.BlockComment)
            {
                if (current == '/' && next == '*')
                {
                    blockDepth++;
                    Step(2);
                    continue;
                }
                if (current == '*' && next == '/')
                {
                    blockDepth--;
                    if (blockDepth == 0)
                        state = State.Normal;
                    Step(2);
                    continue;
                }
            }
            else if (state == State.SingleQuoted && current == '\'')
            {
                if (next == '\'')
                {
                    Step(2);
                    continue;
                }
                state = State.Normal;
            }
            else if (state == State.DoubleQuoted && current == '"')
            {
                if (next == '"')
                {
                    Step(2);
                    continue;
                }
                state = State.Normal;
            }
            else if (state == State.Bracketed && current == ']')
            {
                if (next == ']')
                {
                    Step(2);
                    continue;
                }
                state = State.Normal;
            }
            Step(1);

            void Step(int count)
            {
                for (var step = 0; step < count; step++)
                    Advance(text[index++]);
            }
            void Advance(char character)
            {
                if (character == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                    column++;
            }
        }
        foreach (var input in recipe.Inputs)
        {
            if (!result.Any(token => token.InputName == input.Name))
                throw new RecipeEngineException(
                    "INPUT_UNUSED",
                    $"Input {input.Name} has no ids/table/value placeholder in the SQL template."
                );
            var kinds = result.Where(token => token.InputName == input.Name).Select(token => token.Kind).ToHashSet(StringComparer.Ordinal);
            if (kinds.Contains("value") && kinds.Count > 1)
                throw new RecipeEngineException(
                    "PLACEHOLDER_KIND_MIXED",
                    $"Input {input.Name} is used both as a single value and as a list; use separate inputs."
                );
            if (input.Optional && !kinds.Contains("value"))
                throw new RecipeEngineException(
                    "INPUT_OPTIONAL_VALUE_ONLY",
                    $"Input {input.Name} is optional, but only inputs used as a single value can be left empty."
                );
        }
        return result;
    }

    public static string InputTableName(string name)
    {
        if (!InputRules.IsName(name))
            throw new RecipeEngineException("INPUT_NAME_INVALID", "Invalid input name.");
        return "#schemora_" + name;
    }

    public static string BindForValidation(RecipeDefinition recipe) =>
        Replace(
            recipe,
            token =>
                token.Kind switch
                {
                    "ids" => "SELECT [Value] FROM " + InputRules.Quote(InputTableName(token.InputName)),
                    // A scalar subquery over the same input rowset lets the binder check the value's type against the column it meets.
                    "value" => "(SELECT [Value] FROM " + InputRules.Quote(InputTableName(token.InputName)) + ")",
                    _ => InputRules.Quote(InputTableName(token.InputName)),
                }
        );

    internal static string Replace(RecipeDefinition recipe, Func<RecipeToken, string> replacement)
    {
        var tokens = ValidateDefinition(recipe);
        var sql = new StringBuilder();
        var offset = 0;
        foreach (var token in tokens)
        {
            var value = replacement(token);
            if ((long)sql.Length + token.Start - offset + value.Length > 32 * 1024 * 1024)
                throw new RecipeEngineException("SQL_OUTPUT_TOO_LARGE", "Expanded SQL template exceeds the 64 MiB UTF-16 memory limit.");
            sql.Append(recipe.SqlTemplate, offset, token.Start - offset).Append(value);
            offset = token.Start + token.Length;
        }
        return sql.Append(recipe.SqlTemplate, offset, recipe.SqlTemplate.Length - offset).ToString();
    }

    private enum State
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        Bracketed,
        LineComment,
        BlockComment,
    }
}
