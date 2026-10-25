using System.Text.Json;
using System.Text.RegularExpressions;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record DockerFailureReport(
    string Id,
    DateTimeOffset At,
    string Stage,
    long ElapsedMs,
    string Code,
    int? ExitCode,
    string Output
);

public static class DockerFailureDiagnostics
{
    public static IReadOnlyList<DockerFailureReport> Read(string dataDirectory)
    {
        var directory = Path.Combine(dataDirectory, "docker-diagnostics");
        if (!Directory.Exists(directory))
            return [];
        LocalFiles.CheckNoLinks(directory);
        var reports = new List<DockerFailureReport>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _) || file.Length > 32768)
                continue;
            LocalFiles.CheckNoLinks(file.FullName);
            try
            {
                var report = JsonSerializer.Deserialize<DockerFailureReport>(File.ReadAllText(file.FullName), CatalogStore.Json);
                if (report is not null)
                    reports.Add(report);
            }
            catch (JsonException) { }
        }
        return reports;
    }

    public static string Sanitize(string output, string secret)
    {
        // Redact before truncating: otherwise the boundary could expose part of a password.
        if (!string.IsNullOrEmpty(secret))
            output = output.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        output = Regex.Replace(
            output,
            @"(?i)\b(password|pwd|token|secret|SQLCMDPASSWORD|MSSQL_SA_PASSWORD)\s*[=:]\s*(?:""[^""]*""|'[^']*'|[^\s;]+)",
            "$1=[REDACTED]",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        output = new string(output.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        return output[..Math.Min(output.Length, 4000)];
    }

    public static string Record(string dataDirectory, string stage, long elapsedMs, AppException error, string secret, int? exitCode = null)
    {
        var report = new DockerFailureReport(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            stage,
            elapsedMs,
            error.Code,
            exitCode,
            Sanitize(error.Detail ?? "", secret)
        );
        var detail = JsonSerializer.Serialize(report, CatalogStore.Json);
        try
        {
            var directory = Path.Combine(dataDirectory, "docker-diagnostics");
            LocalFiles.CheckNoLinks(dataDirectory);
            if (!Directory.Exists(directory))
                DockerSchemaReader.CreatePrivateDirectory(directory);
            LocalFiles.CheckNoLinks(directory);
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(19))
                if (
                    Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _)
                    && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                )
                    file.Delete();
            using var stream = new FileStream(
                Path.Combine(directory, report.Id + ".json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            JsonSerializer.Serialize(stream, report, CatalogStore.Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AppException)
        {
            // The API still returns the diagnostic if the local journal cannot be written.
        }
        return detail;
    }
}
