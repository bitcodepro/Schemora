using System.Globalization;
using System.Text;

namespace Schemora.RecipeEngine;

/// <summary>Deterministic, local CSV/TSV/SQL-list tokenizer. Input values are never logged or persisted.</summary>
public sealed class IdProcessor
{
    public const int MaximumCharacters = 8 * 1024 * 1024;
    public const int MaximumValues = 100_000;

    public IdParseResult Parse(string text, RecipeInputDefinition input)
    {
        ArgumentNullException.ThrowIfNull(text);
        InputRules.Validate(input);
        if (text.Length > MaximumCharacters)
            throw new RecipeEngineException("ID_INPUT_TOO_LARGE", "ID input exceeds 8 Mi characters.");
        var tokens = Tokenize(text);
        if (tokens.Count > MaximumValues)
            throw new RecipeEngineException("ID_COUNT_LIMIT", "ID input exceeds 100000 values.");
        var errors = new List<IdIssue>();
        var valid = new List<ProcessedId>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<ProcessedId>();
        var invalid = 0;
        foreach (var token in tokens)
        {
            var issue = token.Error is null
                ? ValidateValue(token.Value, input)
                : (token.Error, "Malformed quoted value or unexpected text after a closing quote.");
            if (issue.Item1 is not null)
            {
                invalid++;
                if (errors.Count < 1000)
                    errors.Add(
                        new(
                            token.Index,
                            token.Line,
                            token.Column,
                            issue.Item1,
                            issue.Item2,
                            token.Raw.Length <= 256 ? token.Raw : token.Raw[..256] + "…"
                        )
                    );
                continue;
            }
            var value = new ProcessedId(token.Index, token.Line, token.Column, token.Value);
            valid.Add(value);
            // Numbers, dates and bits compare by value; GUID/string casing and Unicode are intentionally not normalized.
            var first = unique.Add(InputRules.Canonical(input, token.Value));
            if (!input.Deduplicate || first)
                output.Add(value);
        }
        if (!input.PreserveOrder)
            output = InputRules.Type(input) is "int" or "bigint"
                ? output
                    .OrderBy(value => long.Parse(value.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
                    .ThenBy(value => value.Index)
                    .ToList()
                : output
                    .OrderBy(value => InputRules.Canonical(input, value.Value), StringComparer.Ordinal)
                    .ThenBy(value => value.Index)
                    .ToList();
        return new(input, new(tokens.Count, valid.Count, invalid, unique.Count, valid.Count - unique.Count, output.Count), output, errors);
    }

    private static (string? Code, string Message) ValidateValue(string value, RecipeInputDefinition input)
    {
        if (value.Length == 0)
            return ("EMPTY_ID", "Empty values are not valid identifiers.");
        var type = InputRules.Type(input);
        if (type is "int" or "bigint")
        {
            var digits = value[0] is '+' or '-' ? value.AsSpan(1) : value.AsSpan();
            if (digits.IsEmpty || !digits.ContainsAnyExceptInRange('0', '9'))
            {
                if (type == "int" && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    return (null, "");
                if (type == "bigint" && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    return (null, "");
            }
            return ("INTEGER_INVALID", $"Value must be a signed {type} within its SQL Server range, using ASCII digits.");
        }
        if (type == "uniqueidentifier")
            return value.Length == 36 && Guid.TryParseExact(value, "D", out _)
                ? (null, "")
                : ("GUID_INVALID", "Use a 36-character D-format GUID with hyphens; its original casing is preserved.");
        if (type == "date")
            return InputRules.TryDate(value, out _) ? (null, "") : ("DATE_INVALID", "Use an ISO date: yyyy-MM-dd.");
        if (type == "datetime2")
            return InputRules.TryDateTime(value, out _)
                ? (null, "")
                : ("DATETIME_INVALID", "Use an ISO date and time: yyyy-MM-ddTHH:mm[:ss[.fffffff]]; quote values that contain a space.");
        if (type == "bit")
            return InputRules.TryBit(value, out _) ? (null, "") : ("BIT_INVALID", "Use 1, 0, true or false.");
        if (type == "decimal")
            return InputRules.TryDecimal(input, value, out _)
                ? (null, "")
                : ("DECIMAL_INVALID", "Use digits with '.' as the decimal separator, within the declared precision and scale.");
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\0')
                return ("TEXT_NUL_INVALID", "NUL characters are not supported in SQL text identifiers.");
            if (char.IsSurrogate(value[index]))
            {
                if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    return ("UNICODE_INVALID", "The value contains an incomplete Unicode surrogate pair.");
                index++;
            }
        }
        if (type == "nvarchar")
            return input.MaxLength != -1 && value.Length > input.MaxLength
                ? ("TEXT_LENGTH_EXCEEDED", "NVARCHAR length is measured in UTF-16 code units; the value exceeds the declared length.")
                : (null, "");
        try
        {
            var size = InputRules.TextEncoding(input).GetByteCount(value);
            if (input.MaxLength != -1 && size > input.MaxLength)
                return ("TEXT_LENGTH_EXCEEDED", "VARCHAR length is measured in bytes in the declared code page; the value exceeds it.");
            if (value.Any(character => character > 127) && (input.Encoding is null || input.Collation is null))
                return (
                    "VARCHAR_COLLATION_REQUIRED",
                    "Non-ASCII VARCHAR values require an explicit supported encoding and matching SQL collation; use NVARCHAR when the target code page is unknown."
                );
            return (null, "");
        }
        catch (EncoderFallbackException)
        {
            return ("TEXT_ENCODING_INVALID", "The identifier is not representable in the declared VARCHAR encoding.");
        }
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var start = 0;
        var end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start]) && text[start] != '\t')
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]) && text[end - 1] != '\t')
            end--;
        if (end - start >= 4 && text.AsSpan(start, 2).Equals("IN", StringComparison.OrdinalIgnoreCase))
        {
            var prefixEnd = start + 2;
            while (prefixEnd < end && char.IsWhiteSpace(text[prefixEnd]))
                prefixEnd++;
            if (prefixEnd < end && text[prefixEnd] == '(' && text[end - 1] == ')')
                start = prefixEnd;
        }
        if (start < end && text[start] == '(' && text[end - 1] == ')')
        {
            start++;
            end--;
        }
        var index = start;
        var line = 1;
        var column = 1;
        for (var position = 0; position < start; position++)
            AdvancePosition(text[position]);
        var pendingStrong = false;
        while (index < end)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character) && character != '\t')
            {
                Step();
                continue;
            }
            if (Strong(character))
            {
                if (pendingStrong || tokens.Count == 0)
                    AddEmpty();
                pendingStrong = true;
                Step();
                continue;
            }
            var tokenStart = index;
            var tokenLine = line;
            var tokenColumn = column;
            var value = new StringBuilder();
            string? error = null;
            var quote = ClosingQuote(character);
            if ((character is 'N' or 'n') && index + 1 < end && text[index + 1] == '\'')
            {
                Step();
                quote = '\'';
            }
            if (quote != '\0')
            {
                Step();
                var closed = false;
                while (index < end)
                {
                    if (text[index] == quote)
                    {
                        Step();
                        if (index < end && text[index] == quote)
                        {
                            value.Append(quote);
                            Step();
                            continue;
                        }
                        closed = true;
                        break;
                    }
                    value.Append(text[index]);
                    Step();
                }
                if (!closed)
                    error = "UNTERMINATED_QUOTE";
                if (index < end && !char.IsWhiteSpace(text[index]) && !Strong(text[index]))
                {
                    error = "QUOTED_VALUE_SUFFIX";
                    while (index < end && !char.IsWhiteSpace(text[index]) && !Strong(text[index]))
                    {
                        value.Append(text[index]);
                        Step();
                    }
                }
            }
            else
                while (index < end && !char.IsWhiteSpace(text[index]) && !Strong(text[index]))
                {
                    value.Append(text[index]);
                    Step();
                }
            tokens.Add(new(tokens.Count + 1, tokenLine, tokenColumn, text[tokenStart..index], value.ToString(), error));
            if (tokens.Count > MaximumValues)
                throw new RecipeEngineException("ID_COUNT_LIMIT", "ID input exceeds 100000 values.");
            pendingStrong = false;
        }
        if (pendingStrong)
            AddEmpty();
        return tokens;

        void AddEmpty()
        {
            tokens.Add(new(tokens.Count + 1, line, column, "", "", null));
            if (tokens.Count > MaximumValues)
                throw new RecipeEngineException("ID_COUNT_LIMIT", "ID input exceeds 100000 values.");
        }
        void Step()
        {
            AdvancePosition(text[index]);
            index++;
        }
        void AdvancePosition(char value)
        {
            if (value == '\n')
            {
                line++;
                column = 1;
            }
            else
                column++;
        }
        static bool Strong(char value) => value is ',' or ';' or '\t';
        static char ClosingQuote(char value) =>
            value switch
            {
                '\'' => '\'',
                '"' => '"',
                '‘' => '’',
                '“' => '”',
                _ => '\0',
            };
    }

    private sealed record Token(int Index, int Line, int Column, string Raw, string Value, string? Error);
}
