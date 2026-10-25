using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Schemora.Host;

public sealed partial class BuildInputPlanner
{
    internal sealed record SqlIncludeReference(int Line, string Path);

    internal sealed record PlannedSql(string Path, IReadOnlyList<SqlIncludeReference> Includes);

    private sealed partial class Session
    {
        private readonly Dictionary<string, List<SqlIncludeReference>> sqlIncludes = new(Paths);
        private readonly HashSet<string> inspectedSql = new(StringComparer.Ordinal);

        private async Task Evaluate(string path, Dictionary<string, string> props, string project, int depth)
        {
            ct.ThrowIfCancellationRequested();
            path = System.IO.Path.GetFullPath(path);
            if (depth > 64)
            {
                Problem("BUILD_IMPORT_DEPTH", "Import closure exceeds 64 levels: " + path);
                return;
            }
            if (!xmlContexts.Add(IdentityPath(project) + "\0" + IdentityPath(path)))
                return;
            var document = await Xml(path);
            if (document?.Root is null)
                return;
            var priorDirectory = props.GetValueOrDefault("MSBuildThisFileDirectory");
            props["MSBuildThisFileDirectory"] = System.IO.Path.GetDirectoryName(path)! + System.IO.Path.DirectorySeparatorChar;
            props["MSBuildThisFileFullPath"] = path;
            await Nodes(document.Root.Elements());
            if (priorDirectory is null)
                props.Remove("MSBuildThisFileDirectory");
            else
                props["MSBuildThisFileDirectory"] = priorDirectory;
            props["MSBuildThisFileFullPath"] = project;
            async Task Nodes(IEnumerable<XElement> nodes)
            {
                foreach (var node in nodes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Condition(node.Attribute("Condition")?.Value, props, path))
                        continue;
                    switch (node.Name.LocalName)
                    {
                        case "PropertyGroup":
                            foreach (var property in node.Elements())
                                if (
                                    Condition(property.Attribute("Condition")?.Value, props, path)
                                    && !overrides.ContainsKey(property.Name.LocalName)
                                    && property.Name.LocalName is not ("Configuration" or "Platform")
                                )
                                    props[property.Name.LocalName] = Expand(property.Value, props, path, false) ?? property.Value;
                            break;
                        case "ImportGroup":
                            await Nodes(node.Elements());
                            break;
                        case "Import":
                            var raw = node.Attribute("Project")?.Value;
                            if (string.IsNullOrWhiteSpace(raw))
                            {
                                Problem("BUILD_IMPORT_UNRESOLVED", "An import has no literal Project path: " + path);
                                break;
                            }
                            if (
                                raw.StartsWith("$(SQLDBExtensionsRefPath)", StringComparison.OrdinalIgnoreCase)
                                && raw.EndsWith("Microsoft.Data.Tools.Schema.SqlTasks.targets", StringComparison.OrdinalIgnoreCase)
                            )
                            {
                                var system = Expand(raw, props, path, false);
                                if (system is not null && System.IO.Path.IsPathFullyQualified(system) && File.Exists(system))
                                    await Add(system, "toolchain-reference");
                                break;
                            }
                            foreach (var imported in await PathsFor(raw, props, path, true))
                                await Evaluate(imported, props, project, depth + 1);
                            break;
                        case "ItemGroup":
                            foreach (var item in node.Elements())
                            {
                                if (!Condition(item.Attribute("Condition")?.Value, props, path))
                                    continue;
                                if (item.Name.LocalName == "GlobalPackageReference")
                                {
                                    Problem(
                                        "BUILD_PACKAGE_GLOBAL_UNVERIFIED",
                                        "Global package references require an unverified restore closure: " + path
                                    );
                                    continue;
                                }
                                if (item.Name.LocalName == "PackageVersion")
                                {
                                    CentralPackage(item, props, path, project);
                                    continue;
                                }
                                if (item.Name.LocalName == "PackageReference")
                                {
                                    await Package(item, props, path, project);
                                    continue;
                                }
                                var include = item.Attribute("Include")?.Value;
                                if (string.IsNullOrWhiteSpace(include))
                                    continue;
                                if (item.Name.LocalName == "SqlCmdVariable")
                                {
                                    var value = item.Elements().FirstOrDefault(e => e.Name.LocalName == "DefaultValue")?.Value;
                                    if (value is not null && !props.ContainsKey(include))
                                        props[include] = Expand(value, props, path, false) ?? value;
                                    continue;
                                }
                                if (
                                    item.Name.LocalName
                                    is not (
                                        "ProjectReference"
                                        or "ArtifactReference"
                                        or "Build"
                                        or "Content"
                                        or "None"
                                        or "PreDeploy"
                                        or "PostDeploy"
                                    )
                                )
                                    continue;
                                var hint = item.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
                                var requested =
                                    item.Name.LocalName == "ArtifactReference" && !string.IsNullOrWhiteSpace(hint) ? hint : include;
                                foreach (var input in await PathsFor(requested, props, project, false))
                                {
                                    if (item.Name.LocalName == "ProjectReference")
                                        await Project(input);
                                    else
                                    {
                                        await Add(input, Kind(input));
                                        if (input.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                                            await SqlInputs(input, props, project, 0);
                                    }
                                }
                            }
                            break;
                        case "Choose":
                            var selected =
                                node.Elements()
                                    .FirstOrDefault(e =>
                                        e.Name.LocalName == "When" && Condition(e.Attribute("Condition")?.Value, props, path)
                                    )
                                ?? node.Elements().FirstOrDefault(e => e.Name.LocalName == "Otherwise");
                            if (selected is not null)
                                await Nodes(selected.Elements());
                            break;
                        case "Target":
                            // Custom code can consume undeclared files, environment or network input.
                            // Message-only targets and static item/property declarations are auditable.
                            if (
                                node.Descendants().Any(e => e.Name.LocalName == "UsingTask")
                                || node.Elements().Any(e => e.Name.LocalName is not ("Message" or "PropertyGroup" or "ItemGroup"))
                            )
                                Problem("BUILD_CUSTOM_TASK_INPUTS_UNVERIFIED", "A custom target can consume undeclared inputs: " + path);
                            await Nodes(node.Elements().Where(e => e.Name.LocalName is "PropertyGroup" or "ItemGroup"));
                            break;
                        case "UsingTask":
                            Problem(
                                "BUILD_CUSTOM_TASK_INPUTS_UNVERIFIED",
                                "A custom MSBuild task has an unverified input closure: " + path
                            );
                            break;
                    }
                }
            }
        }

        private async Task<IReadOnlyList<string>> PathsFor(string raw, Dictionary<string, string> props, string xml, bool import)
        {
            var expanded = Expand(raw, props, xml, true);
            if (expanded is null)
                return [];
            var result = new List<string>();
            foreach (var part in expanded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var normalized = part.Replace('\\', System.IO.Path.DirectorySeparatorChar)
                        .Replace('/', System.IO.Path.DirectorySeparatorChar);
                    var path = System.IO.Path.GetFullPath(normalized, System.IO.Path.GetDirectoryName(xml)!);
                    if (path.IndexOfAny(['*', '?']) >= 0)
                    {
                        var wildcard = path.IndexOfAny(['*', '?']);
                        var split = path.LastIndexOf(System.IO.Path.DirectorySeparatorChar, wildcard);
                        var directory = path[..(split + 1)];
                        var pattern = path[(split + 1)..].Replace('\\', '/');
                        if (!Directory.Exists(directory))
                        {
                            if (import)
                                Problem("BUILD_IMPORT_MISSING", "Import directory is missing: " + xml);
                            continue;
                        }
                        var matches = new List<string>();
                        var pending = new Stack<string>();
                        pending.Push(directory);
                        var count = 0;
                        var regex = new Regex(
                            "^"
                                + Regex
                                    .Escape(pattern)
                                    .Replace(@"\*\*/", "(?:.*/)?")
                                    .Replace(@"\*\*", ".*")
                                    .Replace(@"\*", "[^/]*")
                                    .Replace(@"\?", "[^/]")
                                + "$",
                            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
                            TimeSpan.FromMilliseconds(100)
                        );
                        while (pending.TryPop(out var current))
                        {
                            Local(current);
                            foreach (var child in Directory.EnumerateFileSystemEntries(current))
                            {
                                ct.ThrowIfCancellationRequested();
                                if (++count > 20_000)
                                    throw new IOException("Glob entry limit.");
                                var attributes = File.GetAttributes(child);
                                if ((attributes & FileAttributes.ReparsePoint) != 0)
                                    throw new IOException("Glob contains a link.");
                                if ((attributes & FileAttributes.Directory) != 0)
                                {
                                    if (
                                        pattern.Contains("**", StringComparison.Ordinal)
                                        && !Excluded.Contains(System.IO.Path.GetFileName(child))
                                    )
                                        pending.Push(child);
                                }
                                else if (regex.IsMatch(System.IO.Path.GetRelativePath(directory, child).Replace('\\', '/')))
                                    matches.Add(child);
                            }
                        }
                        result.AddRange(matches.Order(Paths));
                    }
                    else if (File.Exists(path))
                    {
                        Local(path);
                        result.Add(path);
                    }
                    else
                        Problem(
                            MissingPackageInput(path) ? "BUILD_PACKAGE_RESTORE_REQUIRED"
                                : import ? "BUILD_IMPORT_MISSING"
                                : "BUILD_REFERENCE_MISSING",
                            "A declared file is missing: " + path
                        );
                }
                catch (Exception ex) when (Io(ex))
                {
                    Problem("BUILD_PATH_UNSUPPORTED", "A declared path cannot be resolved safely in: " + xml);
                }
            }
            return result;
        }

        private string? Expand(string text, Dictionary<string, string> props, string path, bool strict)
        {
            var current = text;
            for (var pass = 0; pass < 16 && current.Contains("$(", StringComparison.Ordinal); pass++)
            {
                var changed = false;
                current = Regex.Replace(
                    current,
                    @"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)",
                    match =>
                    {
                        var name = match.Groups[1].Value;
                        if (props.TryGetValue(name, out var value))
                        {
                            changed = true;
                            return value;
                        }
                        var environment = Environment.GetEnvironmentVariable(name);
                        environmentHashes[name] = Schemora.Workspaces.Identity.Hash(environment ?? "");
                        changed = true;
                        return environment ?? "";
                    },
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)
                );
                if (!changed)
                    break;
            }
            if (
                current.Contains("$(", StringComparison.Ordinal)
                || current.Contains("@(", StringComparison.Ordinal)
                || current.Contains("%(", StringComparison.Ordinal)
            )
            {
                if (strict)
                    Problem(
                        "BUILD_DYNAMIC_INPUT_UNRESOLVED",
                        "A computed/property-function input cannot be resolved without executing MSBuild: " + path
                    );
                return null;
            }
            return current;
        }

        private bool Condition(string? condition, Dictionary<string, string> props, string path)
        {
            if (string.IsNullOrWhiteSpace(condition))
                return true;
            var expanded = Expand(condition, props, path, true);
            if (expanded is null)
                return false;
            return Test(expanded.Trim());
            bool Test(string value)
            {
                var or = Regex.Split(value, @"\s+[Oo][Rr]\s+");
                if (or.Length > 1)
                    return or.Any(v => Test(v.Trim()));
                var and = Regex.Split(value, @"\s+[Aa][Nn][Dd]\s+");
                if (and.Length > 1)
                    return and.All(v => Test(v.Trim()));
                var exists = Regex.Match(
                    value,
                    @"^(!?)\s*Exists\s*\(\s*['""]([^'""]+)['""]\s*\)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                );
                if (exists.Success)
                {
                    try
                    {
                        var candidate = System.IO.Path.GetFullPath(
                            exists.Groups[2].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar),
                            System.IO.Path.GetDirectoryName(path)!
                        );
                        var found = File.Exists(candidate) || Directory.Exists(candidate);
                        return exists.Groups[1].Value.Length == 0 ? found : !found;
                    }
                    catch (Exception ex) when (Io(ex))
                    {
                        Problem("BUILD_CONDITION_UNRESOLVED", "An Exists condition has an unsupported path in: " + path);
                        return false;
                    }
                }
                var comparison = Regex.Match(value, @"^\s*(['""])(.*?)\1\s*(==|!=)\s*(['""])(.*?)\4\s*$", RegexOptions.CultureInvariant);
                if (comparison.Success)
                {
                    var equal = string.Equals(comparison.Groups[2].Value, comparison.Groups[5].Value, StringComparison.OrdinalIgnoreCase);
                    return comparison.Groups[3].Value == "==" ? equal : !equal;
                }
                if (bool.TryParse(value, out var boolean))
                    return boolean;
                Problem("BUILD_CONDITION_UNRESOLVED", "An MSBuild condition is outside the supported static subset in: " + path);
                return false;
            }
        }

        private async Task SqlInputs(string path, Dictionary<string, string> props, string project, int depth)
        {
            if (depth > 64)
            {
                Problem("BUILD_SQLCMD_DEPTH", "SQLCMD include nesting exceeds 64: " + path);
                return;
            }
            if (!inspectedSql.Add(IdentityPath(project) + "\0" + IdentityPath(path)))
                return;
            await Add(path, "sql");
            try
            {
                var contents = await ReadVerified(path);
                if (contents is null)
                    return;
                using var input = new MemoryStream(contents, false);
                using var stream = new StreamReader(input);
                var includes = new List<SqlIncludeReference>();
                var line = 0;
                while (await stream.ReadLineAsync(ct) is { } text)
                {
                    line++;
                    var set = Regex.Match(
                        text,
                        @"^\s*:setvar\s+([A-Za-z_][A-Za-z0-9_]*)\s+""([^""]*)""\s*$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    );
                    if (set.Success)
                    {
                        props[set.Groups[1].Value] = set.Groups[2].Value;
                        continue;
                    }
                    var match = Regex.Match(
                        text,
                        @"^\s*:r\s+(?:""([^""]+)""|(.+?))\s*$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    );
                    if (!match.Success)
                        continue;
                    var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    var resolved = await PathsFor(raw, props, path, false);
                    if (resolved.Count != 1)
                    {
                        Problem("BUILD_SQLCMD_INCLUDE_UNRESOLVED", "A SQLCMD include is missing or ambiguous: " + path);
                        continue;
                    }
                    includes.Add(new(line, resolved[0]));
                    await SqlInputs(resolved[0], props, project, depth + 1);
                }
                if (includes.Count > 0)
                {
                    if (sqlIncludes.TryGetValue(path, out var prior) && !prior.SequenceEqual(includes))
                        Problem("BUILD_CONTEXT_DEPENDENT_INCLUDE", "A shared SQLCMD include resolves differently across projects: " + path);
                    else
                        sqlIncludes[path] = includes;
                }
            }
            catch (Exception ex) when (Io(ex))
            {
                Problem("BUILD_SQLCMD_UNREADABLE", "Cannot inspect SQLCMD include closure: " + path);
            }
        }

        private static string IdentityPath(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }
}
