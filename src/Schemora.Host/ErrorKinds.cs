using System.Text.Json;
using System.Xml;

namespace Schemora.Host;

/// <summary>Tells failures caused by the user's files and tools apart from defects in Schemora itself.</summary>
public static class ErrorKinds
{
    public static bool IsInputFailure(Exception error) =>
        error switch
        {
            AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsInputFailure),
            IOException or UnauthorizedAccessException or InvalidDataException or XmlException or JsonException => true,
            _ => error.GetType().Namespace?.StartsWith("Microsoft.SqlServer.Dac", StringComparison.Ordinal) == true
                || error.InnerException is { } inner && IsInputFailure(inner),
        };
}
