using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;

namespace Schemora.Host;

/// <summary>Read-only launch settings. Explicit command-line port overrides the JSON value.</summary>
public sealed record LaunchConfiguration(int Port, string ConfigPath)
{
    public const string FileName = "schemora.json";
    public const int MaximumFileBytes = 16 * 1024;

    public static LaunchConfiguration Load(IReadOnlyList<string> arguments, string? applicationDirectory = null)
    {
        var configArgument = Option(arguments, "--config");
        var portArgument = Option(arguments, "--port");
        string path;
        try
        {
            path = Path.GetFullPath(configArgument ?? Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, FileName));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppException("LAUNCH_CONFIG_PATH_INVALID", "Invalid launch configuration path. Use --config <path>.");
        }
        var port = ReadPort(path, configArgument is not null);
        if (portArgument is not null)
        {
            if (
                portArgument.Length is < 1 or > 5
                || portArgument.Any(character => character is < '0' or > '9')
                || !int.TryParse(portArgument, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port > 65535
            )
                throw new AppException("LAUNCH_PORT_INVALID", $"--port must be an integer from 0 to 65535. Configuration: {path}");
        }
        return new(port, path);
    }

    private static int ReadPort(string path, bool explicitPath)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumFileBytes)
                throw new AppException("LAUNCH_CONFIG_TOO_LARGE", $"Launch configuration exceeds 16 KiB: {path}");
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Invalid(path, "Expected an object with one integer property: port.");
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "port")
                throw Invalid(path, "Expected exactly one property named port; unknown or duplicate properties are not accepted.");
            var value = properties[0].Value;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var port) || port is < 0 or > 65535)
                throw Invalid(path, "port must be an integer from 0 to 65535; 0 selects a free port.");
            return port;
        }
        catch (FileNotFoundException)
        {
            if (!explicitPath)
                return 0;
            throw new AppException("LAUNCH_CONFIG_NOT_FOUND", $"Launch configuration not found: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            if (!explicitPath)
                return 0;
            throw new AppException("LAUNCH_CONFIG_NOT_FOUND", $"Launch configuration not found: {path}");
        }
        catch (JsonException)
        {
            throw new AppException("LAUNCH_CONFIG_INVALID_JSON", $"Invalid JSON in launch configuration: {path}. Expected {{\"port\":0}}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AppException(
                "LAUNCH_CONFIG_UNREADABLE",
                $"Cannot read launch configuration: {path}. Check file permissions and availability."
            );
        }
    }

    private static AppException Invalid(string path, string reason) => new("LAUNCH_CONFIG_INVALID", $"{reason} Configuration: {path}");

    private static string? Option(IReadOnlyList<string> arguments, string name)
    {
        string? result = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument != name && !argument.StartsWith(name + "=", StringComparison.Ordinal))
                continue;
            if (result is not null)
                throw new AppException("LAUNCH_ARGUMENT_INVALID", $"Specify {name} only once.");
            var value =
                argument == name
                    ? index + 1 < arguments.Count
                        ? arguments[++index]
                        : ""
                    : argument[(name.Length + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new AppException("LAUNCH_ARGUMENT_INVALID", $"Missing value for {name}.");
            result = value;
        }
        return result;
    }

    public static bool IsAddressInUse(Exception error) =>
        error is AddressInUseException
        || error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAddressInUse)
        || error.InnerException is { } inner && IsAddressInUse(inner);

    public string AddressInUseMessage() =>
        $"LAUNCH_PORT_IN_USE: Port {Port} on 127.0.0.1 is already in use. "
        + $"Configuration: {ConfigPath}. Choose another port in this JSON file or use --port <number>; 0 selects a free port.";
}
