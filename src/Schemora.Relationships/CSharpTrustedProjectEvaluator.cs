using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Schemora.SqlServer;

namespace Schemora.Relationships;

/// <summary>Explicit trust boundary: MSBuild evaluation can execute project property functions and imports.</summary>
public sealed class CSharpTrustedProjectEvaluator
{
    public async Task<CSharpProjectContext> EvaluateAsync(
        CSharpProjectSelection selection,
        bool trustedProjectEvaluation,
        CancellationToken ct = default
    )
    {
        if (!trustedProjectEvaluation)
            throw new InvalidOperationException("Explicit trust is required before MSBuild project evaluation.");
        var fallback = new CSharpProjectContextEvaluator().Evaluate(selection, ct);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(selection.ProjectPath))!,
        };
        foreach (
            var arg in new[]
            {
                "msbuild",
                Path.GetFullPath(selection.ProjectPath),
                "-nologo",
                "-getProperty:DefineConstants,TargetFramework,LangVersion,MSBuildAllProjects",
                "-getItem:Compile,ReferencePath,ProjectReference",
                $"-property:Configuration={selection.Configuration}",
            }
        )
            start.ArgumentList.Add(arg);
        if (selection.TargetFramework is not null)
            start.ArgumentList.Add($"-property:TargetFramework={selection.TargetFramework}");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start trusted project evaluation.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = ReadBounded(process.StandardOutput, timeout.Token);
        var errorTask = ReadBounded(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new IOException($"Trusted MSBuild evaluation failed ({process.ExitCode}). {error[..Math.Min(error.Length, 2048)]}");
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        var defines =
            root.GetProperty("Properties")
                .GetProperty("DefineConstants")
                .GetString()
                ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        var dir = start.WorkingDirectory;
        string ItemPath(JsonElement item) =>
            Path.GetFullPath(
                item.TryGetProperty("FullPath", out var full)
                    ? full.GetString()!
                    : Path.Combine(dir, item.GetProperty("Identity").GetString()!)
            );
        var language = root.GetProperty("Properties").GetProperty("LangVersion").GetString() ?? "latest";
        var files = root.GetProperty("Items")
            .GetProperty("Compile")
            .EnumerateArray()
            .Select(i => new CSharpProjectFile(ItemPath(i), defines, language))
            .ToArray();
        var references = root.GetProperty("Items")
            .GetProperty("ReferencePath")
            .EnumerateArray()
            .Select(ItemPath)
            .Where(File.Exists)
            .ToArray();
        var inputs = (
            root.GetProperty("Properties").TryGetProperty("MSBuildAllProjects", out var imports)
                ? (imports.GetString() ?? "")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => Path.GetFullPath(Path.Combine(dir, p)))
                : []
        )
            .Concat(fallback.ProjectInputs ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length > 4096 || references.Length > 1024)
            throw new IOException("Trusted evaluation item limit reached.");
        long totalBytes = 0;
        foreach (var path in files.Select(f => f.Path).Concat(references).Concat(inputs))
        {
            CSharpProjectContextEvaluator.Check(path);
            if (!File.Exists(path))
                continue;
            var size = new FileInfo(path).Length;
            if (size > 32 * 1024 * 1024 || (totalBytes += size) > 128 * 1024 * 1024)
                throw new IOException("Trusted context input size limit reached.");
        }
        var diagnostics = new List<SchemaDiagnostic>
        {
            new(
                "warning",
                "CSHARP_PROJECT_PARTIAL",
                "Trusted evaluation selects actual MSBuild items and symbols. Reference resolution, generators and build targets were not run; missing generated sources/package metadata remain partial."
            ),
        };
        // Project references need separate compilation and framework metadata; never silently treat their default configuration as the selected context.
        if (root.GetProperty("Items").GetProperty("ProjectReference").GetArrayLength() > 0)
            diagnostics.Add(
                new(
                    "warning",
                    "CSHARP_PROJECT_REFERENCE_PARTIAL",
                    "Project reference compilation metadata is unavailable without build targets; referenced projects were not compiled or merged."
                )
            );
        var hash = new StringBuilder(fallback.Fingerprint).Append(output);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(file.Path))
                hash.Append(file.Path).Append(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file.Path, ct))));
        }
        foreach (var reference in references)
            hash.Append(reference).Append(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reference, ct))));
        return new(
            selection,
            files,
            references,
            diagnostics,
            false,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString()))),
            inputs
        );
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (text.Length + count > 4 * 1024 * 1024)
                throw new IOException("Trusted evaluation output limit reached.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
