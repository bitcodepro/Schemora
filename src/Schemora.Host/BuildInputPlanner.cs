using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record BuildInput(string Path, string Kind, string Sha256, long Length, DateTimeOffset LastWrite);

public sealed record BuildInputManifest(
    string Id,
    string ProjectPath,
    DateTimeOffset CreatedAt,
    string Configuration,
    IReadOnlyList<BuildInput> Inputs,
    bool Complete,
    IReadOnlyList<SchemaDiagnostic> Diagnostics,
    string Fingerprint,
    IReadOnlyList<string> Projects,
    IReadOnlyDictionary<string, string> EnvironmentHashes,
    IReadOnlyList<BuildPackageReference>? Packages = null,
    BuildToolReceipt? Toolchain = null
);

public sealed record BuildManifestCheck(bool Current, IReadOnlyList<SchemaDiagnostic> Diagnostics);

public sealed record SqlProjectBuildResult(SchemaGraph Graph, BuildInputManifest Manifest);

internal sealed record PlannedXml(string Path, XDocument Document);

internal sealed record BuildPlan(
    BuildInputManifest Manifest,
    IReadOnlyList<PlannedXml> XmlFiles,
    IReadOnlyList<BuildInputPlanner.PlannedSql> SqlFiles
);

/// <summary>Bounded XML/source planner. Never evaluates MSBuild or executes project code.</summary>
public sealed partial class BuildInputPlanner
{
    internal static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "work",
        ".tools",
        "artifacts",
        ".schemora-stage-build",
    };
    internal static readonly string[] InheritedNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "NuGet.Config",
        "global.json",
    ];

    public async Task<BuildInputManifest> Plan(string projectPath, CancellationToken ct = default) =>
        (await Create(projectPath, ct)).Manifest;

    internal Task<BuildPlan> Create(string projectPath, CancellationToken ct, IReadOnlyDictionary<string, string>? properties = null) =>
        new Session(projectPath, properties).Run(ct);

    private sealed partial class Session
    {
        private readonly string rootProject;
        private readonly Dictionary<string, BuildInput> files = new(Paths);
        private readonly Dictionary<string, XDocument> documents = new(Paths);
        private readonly HashSet<string> directories = new(Paths);
        private readonly HashSet<string> projects = new(Paths);
        private readonly HashSet<string> xmlContexts = new(StringComparer.Ordinal);
        private readonly List<SchemaDiagnostic> diagnostics = [];
        private readonly Dictionary<string, string> environmentHashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyDictionary<string, string> overrides;
        private long bytes;
        private bool complete = true;
        private bool? rootSdk;
        private CancellationToken ct;

        public Session(string projectPath, IReadOnlyDictionary<string, string>? properties)
        {
            rootProject = System.IO.Path.GetFullPath(projectPath);
            overrides = properties ?? new Dictionary<string, string>();
        }

        private void Problem(string code, string path)
        {
            complete = false;
            if (diagnostics.Count < 200 && !diagnostics.Any(d => d.Code == code && d.Message == path))
                diagnostics.Add(new("warning", code, path));
        }

        public async Task<BuildPlan> Run(CancellationToken token)
        {
            ct = token;
            ct.ThrowIfCancellationRequested();
            if (overrides.TryGetValue("SchemoraBuildExecutable", out var executable) && File.Exists(executable))
                await Add(executable, "toolchain-reference");
            await Project(rootProject);
            var ordered = files.Values.OrderBy(f => f.Path, Paths).ToArray();
            var projectList = projects.Order(Paths).ToArray();
            var fingerprint = Identity.Hash(
                JsonSerializer.Serialize(
                    new
                    {
                        Project = Identity.PathKey(rootProject),
                        Configuration = "Release",
                        Platform = "AnyCPU",
                        Inputs = ordered.Select(f => new
                        {
                            Path = Identity.PathKey(f.Path),
                            f.Kind,
                            f.Sha256,
                            f.Length,
                        }),
                        Projects = projectList.Select(Identity.PathKey),
                        Environment = environmentHashes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase),
                        Properties = overrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase),
                    },
                    CatalogStore.Json
                )
            );
            var manifest = new BuildInputManifest(
                Guid.NewGuid().ToString("N"),
                rootProject,
                DateTimeOffset.UtcNow,
                "Release",
                ordered,
                complete,
                diagnostics.ToArray(),
                fingerprint,
                projectList,
                new Dictionary<string, string>(environmentHashes, StringComparer.OrdinalIgnoreCase),
                packageReferences.ToArray()
            );
            return new(
                manifest,
                documents.Select(d => new PlannedXml(d.Key, d.Value)).ToArray(),
                sqlIncludes.Select(d => new PlannedSql(d.Key, d.Value)).ToArray()
            );
        }

        private async Task Project(string path)
        {
            ct.ThrowIfCancellationRequested();
            path = System.IO.Path.GetFullPath(path);
            if (!projects.Add(path))
                return;
            if (projects.Count > 64)
            {
                Problem("BUILD_PROJECT_LIMIT", "Dependency closure exceeds 64 SQL projects.");
                return;
            }
            if (!path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase))
            {
                Problem("BUILD_PROJECT_REFERENCE_UNSUPPORTED", "Only SQL project references are supported: " + path);
                return;
            }
            var doc = await Xml(path);
            if (doc?.Root is null)
                return;
            var sdk =
                doc.Root.Attribute("Sdk")?.Value
                ?? doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value;
            var isSdk = sdk?.Split('/')[0].Equals("Microsoft.Build.Sql", StringComparison.OrdinalIgnoreCase) == true;
            var isLegacy = doc.Descendants()
                .Any(e =>
                    e.Name.LocalName == "Import"
                    && e.Attribute("Project")
                        ?.Value.Contains("Microsoft.Data.Tools.Schema.SqlTasks.targets", StringComparison.OrdinalIgnoreCase) == true
                );
            if (!isSdk && !isLegacy)
            {
                Problem("BUILD_PROJECT_FORMAT_UNSUPPORTED", "Unsupported SQL project format: " + path);
                return;
            }
            rootSdk ??= isSdk;
            if (rootSdk != isSdk)
            {
                Problem("BUILD_MIXED_TOOLCHAIN_UNSUPPORTED", "A dependency requires a different SDK/SSDT toolchain: " + path);
                return;
            }
            var directory = System.IO.Path.GetDirectoryName(path)!;
            await DirectoryInputs(directory);
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = "Release",
                ["Platform"] = "AnyCPU",
                ["MSBuildProjectDirectory"] = directory,
                ["MSBuildProjectFullPath"] = path,
                ["MSBuildProjectFile"] = System.IO.Path.GetFileName(path),
                ["MSBuildProjectName"] = System.IO.Path.GetFileNameWithoutExtension(path),
                ["ProjectDir"] = directory + System.IO.Path.DirectorySeparatorChar,
            };
            foreach (var item in overrides)
                props[item.Key] = item.Value;
            var ancestors = new List<DirectoryInfo>();
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            {
                ct.ThrowIfCancellationRequested();
                ancestors.Add(parent);
                foreach (var name in InheritedNames)
                {
                    var candidate = System.IO.Path.Combine(parent.FullName, name);
                    if (File.Exists(candidate))
                    {
                        if (name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase))
                            await NugetConfiguration(candidate);
                        else
                            await Add(candidate, "build-config");
                    }
                }
            }
            // MSBuild automatically imports the nearest Directory.Build.props/targets; their explicit
            // imports can then bring in parent/sibling configuration without copying entire repositories.
            foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
            {
                var found = ancestors.Select(a => System.IO.Path.Combine(a.FullName, name)).FirstOrDefault(File.Exists);
                if (found is not null)
                    await Evaluate(found, props, path, 0);
            }
            await Evaluate(path, props, path, 0);
            var targets = ancestors.Select(a => System.IO.Path.Combine(a.FullName, "Directory.Build.targets")).FirstOrDefault(File.Exists);
            if (targets is not null)
                await Evaluate(targets, props, path, 0);
            foreach (var sql in files.Values.Where(f => f.Kind == "sql" && Within(f.Path, directory)).Select(f => f.Path).ToArray())
                await SqlInputs(sql, props, path, 0);
        }

        private async Task DirectoryInputs(string directory)
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((directory, 0));
            while (pending.TryPop(out var current))
            {
                ct.ThrowIfCancellationRequested();
                if (!directories.Add(current.Path))
                    continue;
                if (current.Depth > 128)
                {
                    Problem("BUILD_DIRECTORY_DEPTH", "Source nesting exceeds 128 levels: " + current.Path);
                    continue;
                }
                try
                {
                    Local(current.Path);
                    foreach (var child in Directory.EnumerateFileSystemEntries(current.Path).Order(Paths))
                    {
                        ct.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Problem("BUILD_LINK_UNSUPPORTED", "Dependency closure contains a link: " + child);
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!Excluded.Contains(System.IO.Path.GetFileName(child)))
                                pending.Push((child, current.Depth + 1));
                        }
                        else
                            await Add(child, Kind(child));
                    }
                }
                catch (Exception ex) when (Io(ex))
                {
                    Problem("BUILD_INPUT_UNREADABLE", "Cannot inspect source directory: " + current.Path);
                }
            }
        }

        private async Task Add(string path, string kind)
        {
            ct.ThrowIfCancellationRequested();
            path = System.IO.Path.GetFullPath(path);
            if (files.ContainsKey(path))
                return;
            if (files.Count >= 20_000)
            {
                Problem("BUILD_INPUT_LIMIT", "Source closure exceeds 20000 files.");
                return;
            }
            try
            {
                Local(path);
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    Problem("BUILD_INPUT_MISSING", "A declared input is missing: " + path);
                    return;
                }
                var length = info.Length;
                var stamp = info.LastWriteTimeUtc;
                if (length > 128L * 1024 * 1024 || bytes + length > 512L * 1024 * 1024)
                {
                    Problem("BUILD_INPUT_SIZE", "Source closure exceeds 128 MiB/file or 512 MiB total: " + path);
                    return;
                }
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                );
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                info.Refresh();
                if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != stamp)
                {
                    Problem("BUILD_INPUT_CHANGED", "Input changed while fingerprinting: " + path);
                    return;
                }
                files[path] = new(path, kind, hash, length, new DateTimeOffset(stamp));
                bytes += length;
            }
            catch (Exception ex) when (Io(ex))
            {
                Problem("BUILD_INPUT_UNREADABLE", "Cannot fingerprint declared input: " + path);
            }
        }

        private async Task<XDocument?> Xml(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            if (documents.TryGetValue(path, out var old))
                return old;
            await Add(path, Kind(path));
            try
            {
                Local(path);
                if (new FileInfo(path).Length > 4L * 1024 * 1024)
                {
                    Problem("BUILD_XML_LIMIT", "Build XML exceeds 4 MiB: " + path);
                    return null;
                }
                var contents = await ReadVerified(path);
                if (contents is null)
                    return null;
                using var input = new MemoryStream(contents, false);
                using var reader = XmlReader.Create(
                    input,
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = 4L * 1024 * 1024,
                    }
                );
                var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                documents[path] = document;
                return document;
            }
            catch (Exception ex) when (Io(ex) || ex is XmlException)
            {
                Problem("BUILD_XML_INVALID", "Cannot safely parse build XML: " + path);
                return null;
            }
        }

        private async Task<byte[]?> ReadVerified(string path)
        {
            var contents = await File.ReadAllBytesAsync(path, ct);
            if (
                !files.TryGetValue(path, out var expected)
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(contents)), expected.Sha256, StringComparison.Ordinal)
            )
            {
                Problem("BUILD_INPUT_CHANGED", "Input changed between fingerprinting and inspection: " + path);
                return null;
            }
            return contents;
        }

        private static bool Within(string file, string directory)
        {
            var relative = System.IO.Path.GetRelativePath(directory, file);
            return !System.IO.Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static void Local(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(path).IsUnc)
                throw new IOException("Network input is unsupported.");
            LocalFiles.CheckNoLinks(path);
        }

        private static bool Io(Exception e) =>
            e is IOException or UnauthorizedAccessException or System.Security.SecurityException or AppException or ArgumentException;

        private static string Kind(string path) =>
            System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".sqlproj" => "project",
                ".props" or ".targets" => "build-config",
                ".dacpac" => "dacpac-reference",
                ".sql" => "sql",
                _ => "staged-file",
            };
    }
}
