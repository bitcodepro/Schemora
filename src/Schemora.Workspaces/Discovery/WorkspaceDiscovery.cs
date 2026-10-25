using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Schemora.Workspaces.Discovery;

public sealed record DiscoveredSource(
    string Id,
    string Path,
    string Kind,
    string Hash,
    DateTimeOffset LastWrite,
    IReadOnlyList<string> Diagnostics,
    long Length = 0
);

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredSource> Sources,
    IReadOnlyList<string> Diagnostics,
    int HashedFiles,
    int ReusedFiles,
    bool Complete = true
);

/// <summary>
/// Read-only, bounded discovery. Git ignore rules are evaluated against working-tree paths;
/// Git's index, global excludes and .git/info/exclude are deliberately not read. POSIX bracket
/// classes are diagnosed as unsupported. Ignored directories cannot be re-included through
/// a child-only negation, matching Git's pruning rule. Reparse points are never followed.
/// </summary>
public sealed class WorkspaceDiscovery
{
    public const long MaximumFileLength = 32L * 1024 * 1024;
    private const int MaximumDepth = 128;
    private const int MaximumEntries = 250_000;
    private const int MaximumDiagnostics = 1_000;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        "node_modules",
        ".vs",
        "dist",
        "work",
        "artifacts",
    };

    public async Task<DiscoveryResult> Scan(
        IReadOnlyList<string> roots,
        IReadOnlyList<string> excludes,
        IReadOnlyDictionary<string, DiscoveredSource>? previous,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(excludes);
        ct.ThrowIfCancellationRequested();
        var sources = new Dictionary<string, DiscoveredSource>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        var visited = new HashSet<string>(PathComparer);
        var rootPaths = new HashSet<string>(PathComparer);
        var hashed = 0;
        var reused = 0;
        var entries = 0;
        var limitReached = false;
        var complete = true;
        void Diagnose(string message)
        {
            if (diagnostics.Count < MaximumDiagnostics)
                diagnostics.Add(message);
            else if (diagnostics.Count == MaximumDiagnostics)
                diagnostics.Add("Further discovery diagnostics were omitted after the first 1000.");
        }
        void Incomplete(string message)
        {
            complete = false;
            Diagnose(message);
        }
        async Task DiscoverFile(FileInfo file, string kind)
        {
            var maximum = kind == "buildConfig" ? 1024L * 1024 : MaximumFileLength;
            if (file.Length > maximum)
            {
                Incomplete($"Skipped source larger than {maximum / 1024 / 1024} MiB: {file.FullName}");
                return;
            }
            var path = NormalizePath(file.FullName);
            var id = StableId(path);
            if (sources.ContainsKey(id))
                return;
            var stamp = new DateTimeOffset(file.LastWriteTimeUtc);
            var length = file.Length;
            var sourceDiagnostics =
                kind == "config" ? new[] { "Configuration discovered by filename only; contents were not read." } : Array.Empty<string>();
            DiscoveredSource? old = null;
            if (previous is not null && !previous.TryGetValue(id, out old))
                previous.TryGetValue(path, out old);
            if (
                old is not null
                && old.Kind == kind
                && old.Length == length
                && old.LastWrite == stamp
                && !string.IsNullOrEmpty(old.Hash)
                && PathComparer.Equals(old.Path, path)
            )
            {
                sources[id] = new(id, path, kind, old.Hash, stamp, sourceDiagnostics, length);
                reused++;
                return;
            }
            string hash;
            if (kind == "config")
                hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"metadata:{stamp.UtcTicks}:{length}")));
            else
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                );
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                hashed++;
                file.Refresh();
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != stamp.UtcDateTime)
                {
                    Incomplete($"Source changed while hashing; skipped until next refresh: {path}");
                    return;
                }
            }
            sources[id] = new(id, path, kind, hash, stamp, sourceDiagnostics, length);
        }

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                rootPaths.Add(NormalizePath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Incomplete($"Invalid workspace root: {root}");
            }
        }

        // Parent roots are processed first so overlapping roots do not produce duplicate sources.
        foreach (var root in rootPaths.OrderBy(x => x.Length).ThenBy(x => x, PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            if (limitReached)
                break;
            DirectoryInfo rootInfo;
            try
            {
                rootInfo = new DirectoryInfo(root);
                if (!rootInfo.Exists)
                {
                    Incomplete($"Workspace root does not exist: {root}");
                    continue;
                }
                if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Diagnose($"Skipped linked workspace root: {root}");
                    continue;
                }
            }
            catch (Exception ex) when (IsIoError(ex))
            {
                Incomplete($"Cannot inspect workspace root {root}: {ex.GetType().Name}");
                continue;
            }
            var userRules = IgnoreRules.Parse(excludes, root, "workspace excludes", Diagnose);
            var stack = new Stack<DirectoryFrame>();
            stack.Push(new(root, [], 0));
            while (stack.Count > 0 && !limitReached)
            {
                ct.ThrowIfCancellationRequested();
                var frame = stack.Pop();
                if (!visited.Add(frame.Path))
                    continue;
                if (frame.Depth > MaximumDepth)
                {
                    Incomplete($"Directory depth limit ({MaximumDepth}) reached: {frame.Path}");
                    continue;
                }
                var rules = new List<IgnoreRule>(frame.Rules);
                var ignorePath = System.IO.Path.Combine(frame.Path, ".gitignore");
                try
                {
                    var ignoreInfo = new FileInfo(ignorePath);
                    if (ignoreInfo.Exists && (ignoreInfo.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        if (ignoreInfo.Length > 1024 * 1024)
                            Incomplete($"Ignored oversized .gitignore (limit 1 MiB): {ignorePath}");
                        else
                        {
                            var lines = await File.ReadAllLinesAsync(ignorePath, ct);
                            if (lines.Length > 10_000)
                                Incomplete($"Only the first 10000 ignore rules were read: {ignorePath}");
                            rules.AddRange(IgnoreRules.Parse(lines.Take(10_000), frame.Path, ignorePath, Diagnose));
                        }
                    }
                }
                catch (Exception ex) when (IsIoError(ex))
                {
                    Incomplete($"Cannot read ignore rules {ignorePath}: {ex.GetType().Name}");
                }

                var children = new List<FileSystemInfo>();
                try
                {
                    foreach (var child in new DirectoryInfo(frame.Path).EnumerateFileSystemInfos())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++entries > MaximumEntries)
                        {
                            Incomplete($"Discovery entry limit ({MaximumEntries}) reached; scan is partial.");
                            limitReached = true;
                            break;
                        }
                        children.Add(child);
                    }
                }
                catch (Exception ex) when (IsIoError(ex))
                {
                    Incomplete($"Cannot enumerate directory {frame.Path}: {ex.GetType().Name}");
                }
                foreach (var child in children.OrderBy(x => x.Name, PathComparer))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = child.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Diagnose($"Skipped link: {child.FullName}");
                            continue;
                        }
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if (child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                        {
                            var repoId = StableId(frame.Path);
                            sources.TryAdd(
                                repoId,
                                new(repoId, frame.Path, "repository", "", new DateTimeOffset(child.LastWriteTimeUtc), [], 0)
                            );
                            continue; // The .git file is never read, and directory internals are never traversed.
                        }
                        if (isDirectory && ExcludedDirectories.Contains(child.Name))
                            continue;
                        if (IgnoreRules.IsIgnored(child.FullName, isDirectory, rules, userRules))
                            continue;
                        if (isDirectory)
                        {
                            stack.Push(new(child.FullName, rules, frame.Depth + 1));
                            continue;
                        }
                        var kind = Classify(child.Name);
                        if (kind is null)
                            continue;
                        await DiscoverFile((FileInfo)child, kind);
                    }
                    catch (Exception ex) when (IsIoError(ex))
                    {
                        Incomplete($"Cannot inspect source {child.FullName}: {ex.GetType().Name}");
                    }
                }
            }
        }
        // The trusted builder stages these inherited names even above workspace roots.
        // Fingerprint their bytes only; never parse settings or execute MSBuild during discovery.
        var inheritedNames = new[]
        {
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
            "NuGet.Config",
            "global.json",
        };
        var inspectedParents = new HashSet<string>(PathComparer);
        foreach (var project in sources.Values.Where(s => s.Kind == "sqlProject").ToArray())
        {
            for (var parent = new DirectoryInfo(System.IO.Path.GetDirectoryName(project.Path)!); parent is not null; parent = parent.Parent)
            {
                ct.ThrowIfCancellationRequested();
                if (!inspectedParents.Add(parent.FullName))
                    break;
                try
                {
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Incomplete($"Cannot fingerprint inherited build configuration through linked directory: {parent.FullName}");
                        break;
                    }
                }
                catch (Exception ex) when (IsIoError(ex))
                {
                    Incomplete($"Cannot inspect inherited build directory {parent.FullName}: {ex.GetType().Name}");
                    break;
                }
                foreach (var name in inheritedNames)
                {
                    if (++entries > MaximumEntries)
                    {
                        Incomplete($"Discovery entry limit ({MaximumEntries}) reached while checking inherited build configuration.");
                        limitReached = true;
                        break;
                    }
                    var candidate = System.IO.Path.Combine(parent.FullName, name);
                    try
                    {
                        var attributes = File.GetAttributes(candidate);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        {
                            Incomplete($"Inherited build configuration is not an ordinary local file: {candidate}");
                            continue;
                        }
                        await DiscoverFile(new FileInfo(candidate), "buildConfig");
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception ex) when (IsIoError(ex))
                    {
                        Incomplete($"Cannot fingerprint inherited build configuration {candidate}: {ex.GetType().Name}");
                    }
                }
                if (limitReached)
                    break;
            }
            if (limitReached)
                break;
        }
        ct.ThrowIfCancellationRequested();
        return new(sources.Values.OrderBy(s => s.Path, PathComparer).ToArray(), diagnostics, hashed, reused, complete);
    }

    private static bool IsIoError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string NormalizePath(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static string StableId(string path)
    {
        var normalized = NormalizePath(path);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string? Classify(string name)
    {
        var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".sln" or ".slnx" => "solution",
            ".csproj" => "csharpProject",
            ".sqlproj" => "sqlProject",
            ".dacpac" => "dacpac",
            ".sql" => "sql",
            ".cs" => "csharp",
            ".props" or ".targets" => "buildConfig",
            _ when name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) => "buildConfig",
            ".json"
                when Regex.IsMatch(
                    name,
                    @"^(appsettings|ormconfig|efcore|efsettings|entityframework)(\..*)?\.json$|\.dbcontext\.json$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                ) => "config",
            _ => null,
        };
    }

    private sealed record DirectoryFrame(string Path, IReadOnlyList<IgnoreRule> Rules, int Depth);
}

internal sealed record IgnoreRule(string BasePath, Regex Pattern, bool DirectoryOnly, bool Negated)
{
    public bool Matches(string path, bool directory)
    {
        var relative = System.IO.Path.GetRelativePath(BasePath, path).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            return false;

        return (!DirectoryOnly || directory) && Pattern.IsMatch(relative);
    }
}

internal static class IgnoreRules
{
    public static IReadOnlyList<IgnoreRule> Parse(IEnumerable<string> lines, string basePath, string source, Action<string> diagnose)
    {
        var result = new List<IgnoreRule>();
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var text = TrimTrailingSpaces(raw);
            if (text.Length == 0 || text[0] == '#')
                continue;
            if (text.Length > 4096)
            {
                diagnose($"Ignore pattern too long at {source}:{lineNumber}");
                continue;
            }
            var negate = text[0] == '!';
            if (negate)
                text = text[1..];
            if (text.Length == 0)
                continue;
            var directoryOnly = text.EndsWith('/');
            if (directoryOnly)
                text = text[..^1];
            var anchored = text.StartsWith('/');
            if (anchored)
                text = text[1..];
            if (text.Length == 0)
                continue;
            if (text.Contains("[:", StringComparison.Ordinal))
            {
                diagnose($"POSIX bracket classes are unsupported at {source}:{lineNumber}");
                continue;
            }
            try
            {
                var prefix = anchored || text.Contains('/') ? "^" : "(?:^|/)";
                var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
                if (OperatingSystem.IsWindows())
                    options |= RegexOptions.IgnoreCase;
                result.Add(
                    new(basePath, new Regex(prefix + Glob(text) + "$", options, TimeSpan.FromMilliseconds(100)), directoryOnly, negate)
                );
            }
            catch (ArgumentException)
            {
                diagnose($"Invalid ignore pattern at {source}:{lineNumber}");
            }
        }
        return result;
    }

    public static bool IsIgnored(string path, bool directory, IReadOnlyList<IgnoreRule> git, IReadOnlyList<IgnoreRule> user)
    {
        var ignored = false;
        foreach (var rule in git)
            if (rule.Matches(path, directory))
                ignored = !rule.Negated;
        foreach (var rule in user)
            if (rule.Matches(path, directory))
                ignored = !rule.Negated;
        return ignored;
    }

    private static string TrimTrailingSpaces(string text)
    {
        while (text.Length > 0 && text[^1] == ' ')
        {
            var escapes = 0;
            for (var i = text.Length - 2; i >= 0 && text[i] == '\\'; i--)
                escapes++;
            if ((escapes & 1) != 0)
                return text.Remove(text.Length - 2, 1);
            text = text[..^1];
        }
        return text;
    }

    private static string Glob(string glob)
    {
        var regex = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '\\':
                    if (++i >= glob.Length)
                        throw new ArgumentException("Trailing escape.");
                    regex.Append(Regex.Escape(glob[i].ToString()));
                    break;
                case '*':
                    var end = i;
                    while (end + 1 < glob.Length && glob[end + 1] == '*')
                        end++;
                    var doubleStar = end - i == 1 && (i == 0 || glob[i - 1] == '/');
                    if (doubleStar && end + 1 < glob.Length && glob[end + 1] == '/')
                    {
                        regex.Append("(?:[^/]+/)*");
                        i = end + 1;
                    }
                    else if (doubleStar && end + 1 == glob.Length)
                    {
                        regex.Append(".*");
                        i = end;
                    }
                    else
                    {
                        regex.Append("[^/]*");
                        i = end;
                    }
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        regex.Append(@"\[");
                        break;
                    }
                    var group = glob[(i + 1)..close];
                    if (group.Length == 0)
                    {
                        regex.Append(@"\[\]");
                        i = close;
                        break;
                    }
                    var negative = group[0] is '!' or '^';
                    if (negative)
                        group = group[1..];
                    if (group.Length == 0 || group.Contains('/'))
                        throw new ArgumentException("Invalid bracket class.");
                    regex.Append('[');
                    if (negative)
                        regex.Append("^/");
                    foreach (var item in group)
                        regex.Append(
                            item switch
                            {
                                '\\' => @"\\",
                                '[' => @"\[",
                                '^' => @"\^",
                                _ => item.ToString(),
                            }
                        );
                    regex.Append(']');
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(glob[i].ToString()));
                    break;
            }
        }
        return regex.ToString();
    }
}
