using System.Text;

namespace Schemora.RecipeEngine;

internal static class InputRules
{
    internal static readonly IReadOnlyDictionary<string, int> Collations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Latin1_General_100_BIN2"] = 1252,
        ["Latin1_General_100_CI_AS"] = 1252,
        ["SQL_Latin1_General_CP1_CI_AS"] = 1252,
        ["Cyrillic_General_100_BIN2"] = 1251,
        ["Cyrillic_General_100_CI_AS"] = 1251,
        ["Latin1_General_100_BIN2_UTF8"] = 65001,
        ["Latin1_General_100_CI_AS_SC_UTF8"] = 65001,
    };

    static InputRules() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static bool IsName(string name) =>
        name.Length is > 0 and <= 64
        && IsAsciiLetter(name[0])
        && name.All(character => IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '_');

    private static bool IsAsciiLetter(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    internal static string Type(RecipeInputDefinition input) => input.Type.ToLowerInvariant();

    internal static void Validate(RecipeInputDefinition input)
    {
        if (!IsName(input.Name))
            throw new RecipeEngineException(
                "INPUT_NAME_INVALID",
                "Input names must be ASCII letters/digits/underscore, start with a letter and contain at most 64 characters."
            );
        var type = Type(input);
        if (type is not ("int" or "bigint" or "uniqueidentifier" or "nvarchar" or "varchar" or "date" or "datetime2" or "bit" or "decimal"))
            throw new RecipeEngineException(
                "INPUT_TYPE_INVALID",
                "Supported input types: int, bigint, uniqueidentifier, nvarchar, varchar, date, datetime2, bit, decimal."
            );
        if (type == "decimal")
        {
            if (
                input.Precision is not { } precision
                || precision is < 1 or > 38
                || input.Scale is not { } scale
                || scale < 0
                || scale > precision
            )
                throw new RecipeEngineException("INPUT_PRECISION_REQUIRED", "decimal requires Precision 1..38 and Scale 0..Precision.");
        }
        else if (input.Precision is not null || input.Scale is not null)
            throw new RecipeEngineException("INPUT_TYPE_OPTIONS_INVALID", "Precision and scale apply only to decimal inputs.");
        if (input.Strategy is not ("auto" or "values" or "temp" or "openjson" or "list"))
            throw new RecipeEngineException("INPUT_STRATEGY_INVALID", "Unknown input SQL strategy.");
        if (type is "nvarchar" or "varchar")
        {
            var maximum = type == "nvarchar" ? 4000 : 8000;
            if (input.MaxLength is not { } length || length != -1 && (length < 1 || length > maximum))
                throw new RecipeEngineException("INPUT_LENGTH_REQUIRED", $"{type} requires MaxLength 1..{maximum}, or -1 for MAX.");
            if (input.Collation is not null && !Collations.ContainsKey(input.Collation))
                throw new RecipeEngineException(
                    "INPUT_COLLATION_UNSUPPORTED",
                    "Choose a supported explicit SQL collation or leave Collation empty for DATABASE_DEFAULT."
                );
            if (type == "varchar")
            {
                var encoding = TextEncoding(input);
                if (input.Collation is not null && encoding.CodePage != 20127 && Collations[input.Collation] != encoding.CodePage)
                    throw new RecipeEngineException(
                        "INPUT_ENCODING_COLLATION_MISMATCH",
                        "The declared VARCHAR code page does not match its explicit SQL collation."
                    );
            }
        }
        else if (input.MaxLength is not null || input.Encoding is not null || input.Collation is not null)
            throw new RecipeEngineException("INPUT_TYPE_OPTIONS_INVALID", "Length, encoding and collation apply only to text inputs.");
    }

    internal static Encoding TextEncoding(RecipeInputDefinition input)
    {
        var codePage = (input.Encoding ?? "ascii").ToLowerInvariant() switch
        {
            "ascii" or "us-ascii" => 20127,
            "utf-8" or "utf8" => 65001,
            "windows-1251" or "cp1251" => 1251,
            "windows-1252" or "cp1252" => 1252,
            _ => throw new RecipeEngineException(
                "INPUT_ENCODING_UNSUPPORTED",
                "Supported VARCHAR encodings: ascii, utf-8, windows-1251, windows-1252."
            ),
        };
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    internal static string SqlType(RecipeInputDefinition input) =>
        Type(input) == "decimal"
            ? $"decimal({input.Precision!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},{input.Scale!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
        : Type(input) is "nvarchar" or "varchar"
            ? $"{Type(input)}({(input.MaxLength == -1 ? "max" : input.MaxLength!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))})"
        : Type(input);

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    internal static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out date
        );

    internal static bool TryDateTime(string value, out DateTime dateTime) =>
        DateTime.TryParseExact(
            value,
            DateTimeFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out dateTime
        );

    /// <summary>
    /// Plain ASCII digits with an optional sign and '.' separator, within the declared precision and scale; the result drops
    /// redundant zeros so equal amounts compare equal. Values beyond System.Decimal's range stay exact because no arithmetic is done.
    /// </summary>
    internal static bool TryDecimal(RecipeInputDefinition input, string value, out string canonical)
    {
        canonical = "";
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^([+-]?)([0-9]+)(?:\.([0-9]+))?$");
        if (!match.Success || input.Precision is not { } precision || input.Scale is not { } scale)
            return false;
        var integer = match.Groups[2].Value.TrimStart('0');
        var fraction = match.Groups[3].Value.TrimEnd('0');
        if (integer.Length > precision - scale || fraction.Length > scale)
            return false;
        var digits = (integer.Length == 0 ? "0" : integer) + (fraction.Length == 0 ? "" : "." + fraction);
        canonical = match.Groups[1].Value == "-" && digits != "0" ? "-" + digits : digits;
        return true;
    }

    internal static bool TryBit(string value, out bool bit)
    {
        bit = value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        return bit || value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One spelling per value for typed inputs, so duplicates, SQL literals and JSON payloads agree; text and GUIDs keep their original form.
    /// </summary>
    internal static string Canonical(RecipeInputDefinition input, string value) =>
        Type(input) switch
        {
            "int" or "bigint" => long.Parse(
                    value,
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture
                )
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "date" when TryDate(value, out var date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            "datetime2" when TryDateTime(value, out var dateTime) => dateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffffff",
                System.Globalization.CultureInfo.InvariantCulture
            ),
            "bit" when TryBit(value, out var bit) => bit ? "1" : "0",
            "decimal" when TryDecimal(input, value, out var number) => number,
            _ => value,
        };

    /// <summary>Explicit CONVERT styles keep date parsing independent of the session's DATEFORMAT and language.</summary>
    internal static string ConvertStyle(RecipeInputDefinition input) =>
        Type(input) switch
        {
            "date" => ", 23",
            "datetime2" => ", 126",
            _ => "",
        };

    internal static string Collate(RecipeInputDefinition input) =>
        Type(input) is "nvarchar" or "varchar" ? " COLLATE " + (input.Collation is null ? "DATABASE_DEFAULT" : input.Collation) : "";

    internal static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
