using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Schemora.Host;

public sealed record BuildToolReceipt(
    string Kind,
    string Executable,
    string ExecutableSha256,
    string? SdkVersion,
    string MsBuildVersion,
    string? MsBuildSha256,
    string? SsdtTargets,
    string? SsdtTargetsSha256,
    string? SsdtTaskVersion,
    string? SqlProjectSdk = null,
    string? SsdtTaskSha256 = null
);

public sealed record AnalysisBuildReceipt(string DatabaseId, string SnapshotId, string ManifestId, BuildToolReceipt? Toolchain);

public static class BuildToolCapture
{
    public static string Hash(string path)
    {
        LocalFiles.CheckNoLinks(path);
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public static async Task<BuildToolReceipt> Capture(
        string executable,
        bool sdk,
        string? ssdtTargets,
        string workingDirectory,
        CancellationToken ct,
        string? sqlProjectSdk = null
    )
    {
        ct.ThrowIfCancellationRequested();
        async Task<string> Version(string[] args)
        {
            var result = await ProcessRunner.Run(
                executable,
                args,
                workingDirectory,
                TimeSpan.FromSeconds(20),
                ct,
                new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1" }
            );
            if (result.ExitCode != 0)
                throw new AppException("BUILD_TOOL_VERSION_UNAVAILABLE", "The selected build tool version could not be verified.", 422);
            var versions = Regex.Matches(
                result.Output,
                @"(?m)^\s*(\d+\.\d+\.\d+[A-Za-z0-9.+-]*)\s*$",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            );
            if (versions.Count == 0)
                throw new AppException(
                    "BUILD_TOOL_VERSION_UNAVAILABLE",
                    "The selected build tool did not return a recognized version.",
                    422
                );
            return versions[^1].Groups[1].Value;
        }
        var sdkVersion = sdk ? await Version(["--version"]) : null;
        // Native MSBuild's /version reports the four-part file version of Microsoft.Build.dll.
        // Read that metadata directly: starting the legacy tool solely for a receipt can stall
        // before the actual build. Do not load the assembly or substitute its assembly/product version.
        var msbuildVersion = sdk ? await Version(["msbuild", "-version", "-nologo"]) : LegacyMsBuildVersion(executable);
        string? msbuildHash = null;
        if (sdk)
        {
            var sdks = await ProcessRunner.Run(executable, ["--list-sdks"], workingDirectory, cancellationToken: ct);
            foreach (var line in sdks.Output.Split('\n'))
            {
                var match = Regex.Match(line.Trim(), @"^(\S+)\s+\[(.+)\]$", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!match.Success || match.Groups[1].Value != sdkVersion)
                    continue;
                var assembly = Path.Combine(match.Groups[2].Value, sdkVersion!, "MSBuild.dll");
                if (File.Exists(assembly))
                    msbuildHash = Hash(assembly);
            }
        }
        else
            msbuildHash = Hash(executable);
        string? ssdtVersion = null;
        string? ssdtHash = null;
        if (ssdtTargets is not null)
        {
            var task = Path.Combine(Path.GetDirectoryName(ssdtTargets)!, "Microsoft.Data.Tools.Schema.Tasks.Sql.dll");
            if (File.Exists(task))
            {
                LocalFiles.CheckNoLinks(task);
                ssdtVersion = FileVersionInfo.GetVersionInfo(task).ProductVersion;
                ssdtHash = Hash(task);
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(
            sdk ? "SDK-style" : "Legacy SSDT",
            executable,
            Hash(executable),
            sdkVersion,
            msbuildVersion,
            msbuildHash,
            ssdtTargets,
            ssdtTargets is null ? null : Hash(ssdtTargets),
            ssdtVersion,
            sqlProjectSdk,
            ssdtHash
        );
    }

    private static string LegacyMsBuildVersion(string executable)
    {
        var engine = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "Microsoft.Build.dll");
        if (!File.Exists(engine))
            throw new AppException("BUILD_TOOL_VERSION_UNAVAILABLE", "The selected build tool version could not be verified.", 422);
        LocalFiles.CheckNoLinks(engine);
        var version = FileVersionInfo.GetVersionInfo(engine);
        if (
            !Version.TryParse(version.FileVersion, out var parsed)
            || parsed.Revision < 0
            || version.FileMajorPart <= 0
            || parsed != new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart)
        )
            throw new AppException(
                "BUILD_TOOL_VERSION_UNAVAILABLE",
                "The selected build tool did not return a recognized version.",
                422
            );
        return parsed.ToString();
    }

    public static IReadOnlyList<AnalysisBuildReceipt> FromSnapshots(
        Schemora.Workspaces.CatalogStore store,
        IReadOnlyList<Schemora.Workspaces.DatabaseInfo> databases,
        IReadOnlyList<Schemora.Workspaces.SnapshotRecord>? pending = null
    )
    {
        var result = new List<AnalysisBuildReceipt>();
        foreach (var db in databases)
        {
            if (db.CurrentSnapshotId is not { } snapshotId)
                continue;
            var snapshot =
                pending?.FirstOrDefault(s => s.Id == snapshotId)
                ?? store.Get<Schemora.Workspaces.SnapshotRecord>("SchemaSnapshots", snapshotId);
            if (snapshot?.BuildManifestId is not { } id || !Guid.TryParseExact(id, "N", out _))
                continue;
            var path = Path.Combine(Path.GetDirectoryName(store.Path)!, "build-manifests", id + ".json");
            BuildToolReceipt? toolchain = null;
            if (File.Exists(path) && new FileInfo(path).Length <= 32L * 1024 * 1024)
            {
                LocalFiles.CheckNoLinks(path);
                toolchain = System
                    .Text.Json.JsonSerializer.Deserialize<BuildInputManifest>(File.ReadAllText(path), Schemora.Workspaces.CatalogStore.Json)
                    ?.Toolchain;
            }
            result.Add(new(db.Id, snapshotId, id, toolchain));
        }
        return result;
    }
}
