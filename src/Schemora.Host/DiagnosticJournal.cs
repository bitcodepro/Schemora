using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record DiagnosticEvent(
    string OperationId,
    DateTimeOffset At,
    string Code,
    int Status,
    string? JobId = null,
    double? Progress = null,
    string? ExceptionType = null,
    IReadOnlyList<string>? Frames = null
);

public sealed record SupportCapability(string Name, bool Available, string? Version);

// Intentionally accepts codes and type/method names, never exception messages, SQL,
// headers, request bodies or process output. Rotation bounds persistent log storage.
public sealed class DiagnosticJournal
{
    private readonly object gate = new();
    private readonly string path;
    private readonly Queue<DiagnosticEvent> events = new();

    public DiagnosticJournal(string dataDirectory)
    {
        var directory = Path.Combine(dataDirectory, "diagnostics");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "events.jsonl");
        foreach (var file in new[] { path + ".1", path })
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 2 * 1024 * 1024)
                continue;
            try
            {
                foreach (var line in File.ReadLines(file).TakeLast(500))
                    if (JsonSerializer.Deserialize<DiagnosticEvent>(line, CatalogStore.Json) is { } entry)
                    {
                        events.Enqueue(entry);
                        while (events.Count > 500)
                            events.Dequeue();
                    }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
    }

    public string Record(
        string code,
        int status,
        string? operationId = null,
        string? jobId = null,
        double? progress = null,
        Exception? error = null
    )
    {
        operationId ??= Guid.NewGuid().ToString("N");
        if (!Regex.IsMatch(code, "^[A-Za-z0-9_:.+-]{1,100}$", RegexOptions.CultureInvariant))
            code = "UNCLASSIFIED";
        var frames = error is null
            ? null
            : new StackTrace(error, false)
                .GetFrames()
                .Take(12)
                .Select(f => f.GetMethod())
                .Where(m => m is not null)
                .Select(m => m!.DeclaringType?.FullName + "." + m.Name)
                .ToArray();
        var entry = new DiagnosticEvent(
            operationId,
            DateTimeOffset.UtcNow,
            code,
            status,
            jobId,
            progress,
            error?.GetType().FullName,
            frames
        );
        lock (gate)
        {
            events.Enqueue(entry);
            while (events.Count > 500)
                events.Dequeue();
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, CatalogStore.Json) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return operationId;
    }

    public IReadOnlyList<DiagnosticEvent> Events(string? jobId = null)
    {
        lock (gate)
            return events.Where(e => jobId is null || e.JobId == jobId).Reverse().Take(200).ToArray();
    }

    public object Report(CatalogStore store, bool includePaths, IReadOnlyList<SupportCapability>? toolchain = null)
    {
        var assembly = Assembly.GetExecutingAssembly();
        return new
        {
            FormatVersion = 1,
            Application = "Schemora",
            Version = assembly.GetName().Version?.ToString(),
            Build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Runtime = Environment.Version.ToString(),
            OperatingSystem = Environment.OSVersion.VersionString,
            Capabilities = new { DotnetAvailable = Toolchain.Dotnet is not null, DockerAvailable = Toolchain.Docker is not null },
            Toolchain = toolchain,
            CurrentSnapshotHashes = store.SnapshotHeaders(),
            PathsIncluded = includePaths,
            Paths = includePaths ? store.All<Workspace>("Workspaces").SelectMany(w => w.Roots).Distinct().ToArray() : null,
            Events = Events(),
            Note = "Messages, SQL, input values, tokens, process output and file contents are excluded.",
        };
    }
}

public sealed record SupportReportRequest(bool IncludePaths = false);

public static class DiagnosticEndpoints
{
    public static void MapDiagnosticEndpoints(this WebApplication app)
    {
        app.MapGet("/api/diagnostics/events", (DiagnosticJournal journal) => journal.Events());
        app.MapGet(
            "/api/jobs/{id}/log",
            (string id, JobQueue jobs, DiagnosticJournal journal) =>
                jobs.Get(id) is null
                    ? Results.NotFound(new { Code = "JOB_NOT_FOUND", Message = "Job not found." })
                    : Results.Ok(journal.Events(id))
        );
        app.MapPost(
            "/api/diagnostics/report",
            async (SupportReportRequest request, CatalogStore store, DiagnosticJournal journal, Toolchain tools, CancellationToken ct) =>
            {
                var diagnostics = JsonSerializer.SerializeToElement(await tools.Diagnose(ct), CatalogStore.Json);
                var capabilities = diagnostics
                    .GetProperty("capabilities")
                    .EnumerateArray()
                    .Select(c =>
                    {
                        var version = c.GetProperty("version").GetString();
                        return new SupportCapability(
                            c.GetProperty("name").GetString()!,
                            c.GetProperty("available").GetBoolean(),
                            version is not null && Regex.IsMatch(version, @"^[0-9][0-9A-Za-z. +_-]{0,100}$") ? version : null
                        );
                    })
                    .ToArray();
                return Results.File(
                    JsonSerializer.SerializeToUtf8Bytes(journal.Report(store, request.IncludePaths, capabilities), CatalogStore.Json),
                    "application/json",
                    "schemora-diagnostics.json"
                );
            }
        );
        app.MapPost(
            "/api/source/open",
            (SourceOpenRequest request, CatalogStore store) =>
            {
                var path = SourceLocation.Validate(store, request.Path);
                var line = Math.Clamp(request.Line ?? 1, 1, 10000000);
                if (request.Editor is not ("vscode" or "visualstudio"))
                    throw new AppException("EDITOR_INVALID", "Choose VS Code or Visual Studio.");
                var executable =
                    request.Editor == "vscode"
                        ? Toolchain.Find(
                            "Code",
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Programs",
                                "Microsoft VS Code",
                                "Code.exe"
                            )
                        )
                        : Toolchain.Find("devenv");
                if (executable is null)
                    throw new AppException("EDITOR_UNAVAILABLE", "The selected editor was not found. Copy the source location instead.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                if (request.Editor == "vscode")
                {
                    start.ArgumentList.Add("--goto");
                    start.ArgumentList.Add(path + ":" + line);
                }
                else
                {
                    start.ArgumentList.Add("/Edit");
                    start.ArgumentList.Add(path);
                    start.ArgumentList.Add("/Command");
                    start.ArgumentList.Add("Edit.Goto " + line);
                }
                Process.Start(start);
                return new { Opened = true };
            }
        );
    }
}

public sealed record SourceOpenRequest(string Path, int? Line, string Editor);

public static class SourceLocation
{
    public static string Validate(CatalogStore store, string input)
    {
        if (!Path.IsPathFullyQualified(input))
            throw new AppException("SOURCE_PATH_INVALID", "Choose a local source file from a workspace.");
        var path = Path.GetFullPath(input);
        if (
            path.StartsWith(@"\\")
            || new Uri(path).IsUnc
            || !new[] { ".sql", ".cs", ".csproj", ".sqlproj", ".props", ".targets" }.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase
            )
        )
            throw new AppException("SOURCE_PATH_INVALID", "Choose a local source file from a workspace.");
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (
            !store
                .All<Workspace>("Workspaces")
                .SelectMany(w => w.Roots)
                .Any(root =>
                    path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparer)
                )
        )
            throw new AppException("SOURCE_OUTSIDE_WORKSPACE", "The source file must be inside a configured workspace root.");
        if (!File.Exists(path))
            throw new AppException("SOURCE_MISSING", "The source file no longer exists.", 404);
        LocalFiles.CheckNoLinks(path);
        return path;
    }
}
