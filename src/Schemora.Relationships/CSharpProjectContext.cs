using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Schemora.SqlServer;

namespace Schemora.Relationships;

public sealed record CSharpProjectSelection(string ProjectPath, string Configuration = "Debug", string? TargetFramework = null);

public sealed record CSharpProjectFile(string Path, IReadOnlyList<string> Defines, string LanguageVersion = "latest");

public sealed record CSharpProjectContext(
    CSharpProjectSelection Selection,
    IReadOnlyList<CSharpProjectFile> Files,
    IReadOnlyList<string> ReferencePaths,
    IReadOnlyList<SchemaDiagnostic> Diagnostics,
    bool IsComplete,
    string Fingerprint,
    IReadOnlyList<string>? ProjectInputs = null
);

/// <summary>Non-executing conservative inspection. No MSBuild, restore, targets or generators are run.</summary>
public sealed class CSharpProjectContextEvaluator
{
    public CSharpProjectContext Evaluate(CSharpProjectSelection selection, CancellationToken ct = default)
    {
        var files = new Dictionary<string, CSharpProjectFile>(StringComparer.OrdinalIgnoreCase);
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<SchemaDiagnostic>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = new StringBuilder().Append(selection);
        long sourceBytes = 0;
        var projectInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Partial(string message) => diagnostics.Add(new("warning", "CSHARP_PROJECT_PARTIAL", message));
        void FingerprintImport(string input)
        {
            var path = Path.GetFullPath(input);
            if (!projectInputs.Add(path))
                return;
            if (projectInputs.Count > 256)
            {
                Partial("Project import fingerprint limit reached.");
                return;
            }
            Check(path);
            evidence.Append(path);
            if (!File.Exists(path))
            {
                evidence.Append("missing");
                return;
            }
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new IOException("Imported project size limit.");
            var content = File.ReadAllText(path);
            evidence.Append(content);
            var root = XDocument.Parse(content).Root;
            if (root is null)
                return;
            foreach (var import in root.Descendants().Where(e => e.Name.LocalName == "Import"))
            {
                var value = ((string?)import.Attribute("Project") ?? "").Replace(
                    "$(MSBuildThisFileDirectory)",
                    Path.GetDirectoryName(path) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                );
                if (value.Length == 0 || value.Contains("$(") || value.IndexOfAny(['*', '?', ';']) >= 0)
                {
                    Partial($"Dynamic import dependencies cannot be fingerprinted statically: {path}.");
                    continue;
                }
                FingerprintImport(Path.Combine(Path.GetDirectoryName(path)!, value));
            }
        }
        void Visit(string input)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(input);
            if (!visited.Add(path))
                return;
            if (visited.Count > 64)
            {
                Partial("Project reference limit reached.");
                return;
            }
            try
            {
                Check(path);
                FingerprintImport(path);
                var text = File.ReadAllText(path);
                var root = XDocument.Parse(text).Root!;
                var dir = Path.GetDirectoryName(path)!;
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Configuration"] = selection.Configuration,
                    ["TargetFramework"] = selection.TargetFramework ?? "",
                    ["Platform"] = "AnyCPU",
                    ["DefineConstants"] = "",
                };
                string Expand(string value) =>
                    Regex.Replace(value, @"\$\(([^)]+)\)", m => props.GetValueOrDefault(m.Groups[1].Value, m.Value));
                bool Applies(XElement node)
                {
                    var c = (string?)node.Attribute("Condition");
                    if (string.IsNullOrWhiteSpace(c))
                        return true;
                    var value = Expand(c);
                    var m = Regex.Match(value, "^\\s*['\"]([^'\"]*)['\"]\\s*(==|!=)\\s*['\"]([^'\"]*)['\"]\\s*$");
                    if (!m.Success || value.Contains("$("))
                    {
                        Partial($"Unsupported condition in {path}: {c}");
                        return false;
                    }
                    var equal = string.Equals(m.Groups[1].Value, m.Groups[3].Value, StringComparison.OrdinalIgnoreCase);
                    return m.Groups[2].Value == "==" ? equal : !equal;
                }
                foreach (var g in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && Applies(e)))
                foreach (var p in g.Elements().Where(Applies))
                    if (p.Name.LocalName != "Configuration" && (p.Name.LocalName != "TargetFramework" || selection.TargetFramework is null))
                        props[p.Name.LocalName] = Expand(p.Value);
                var framework = props.GetValueOrDefault("TargetFramework", "");
                if (framework.Length == 0 && props.TryGetValue("TargetFrameworks", out var frameworks))
                {
                    framework = frameworks.Split(';')[0];
                    Partial($"Select a target framework for {path}.");
                }
                if (
                    selection.TargetFramework is { } requested
                    && props.TryGetValue("TargetFrameworks", out var declared)
                    && !declared.Split(';', StringSplitOptions.TrimEntries).Contains(requested, StringComparer.OrdinalIgnoreCase)
                )
                    Partial($"Selected target framework {requested} is not declared by {path}.");
                var symbols = props
                    .GetValueOrDefault("DefineConstants", "")
                    .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (!props.GetValueOrDefault("DefineTrace", "true").Equals("false", StringComparison.OrdinalIgnoreCase))
                    symbols.Add("TRACE");
                if (
                    selection.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                    && !props.GetValueOrDefault("DefineDebug", "true").Equals("false", StringComparison.OrdinalIgnoreCase)
                )
                    symbols.Add("DEBUG");
                var tfm = Regex.Match(framework, @"^net(\d+)\.(\d+)$");
                if (
                    tfm.Success
                    && int.TryParse(tfm.Groups[1].Value, out var major)
                    && major is >= 5 and <= 30
                    && tfm.Groups[2].Value == "0"
                )
                {
                    if (
                        !props
                            .GetValueOrDefault("DisableImplicitFrameworkDefines", "false")
                            .Equals("true", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        symbols.AddRange([
                            "NET",
                            "NETCOREAPP",
                            "NETCOREAPP1_0_OR_GREATER",
                            "NETCOREAPP1_1_OR_GREATER",
                            "NETCOREAPP2_0_OR_GREATER",
                            "NETCOREAPP2_1_OR_GREATER",
                            "NETCOREAPP2_2_OR_GREATER",
                            "NETCOREAPP3_0_OR_GREATER",
                            "NETCOREAPP3_1_OR_GREATER",
                        ]);
                        symbols.Add($"NET{major}_0");
                        for (var version = 5; version <= major; version++)
                            symbols.Add($"NET{version}_0_OR_GREATER");
                    }
                }
                else
                    Partial($"Framework symbols require review: {framework} in {path}.");
                if (symbols.Any(s => !SyntaxFacts.IsValidIdentifier(s)))
                    Partial($"Unresolved definitions in {path}.");
                symbols = symbols.Where(SyntaxFacts.IsValidIdentifier).Distinct().ToList();
                var language = props.GetValueOrDefault("LangVersion", "latest");
                if (!LanguageVersionFacts.TryParse(language, out _))
                    Partial($"Unsupported C# language version {language} in {path}.");
                var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Walk(string folder)
                {
                    if (sources.Count >= 4096)
                        return;
                    Check(folder);
                    ct.ThrowIfCancellationRequested();
                    foreach (var f in Directory.EnumerateFiles(folder, "*.cs"))
                    {
                        if (sources.Count >= 4096)
                        {
                            Partial("Source limit reached.");
                            return;
                        }
                        sources.Add(f);
                    }
                    foreach (var d in Directory.EnumerateDirectories(folder))
                        if (Path.GetFileName(d) is not ("bin" or "obj" or ".git"))
                            Walk(d);
                }
                if (
                    !props.GetValueOrDefault("EnableDefaultItems", "true").Equals("false", StringComparison.OrdinalIgnoreCase)
                    && !props
                        .GetValueOrDefault("EnableDefaultCompileItems", root.Attribute("Sdk") is null ? "false" : "true")
                        .Equals("false", StringComparison.OrdinalIgnoreCase)
                )
                    Walk(dir);
                foreach (var g in root.Elements().Where(e => e.Name.LocalName == "ItemGroup" && Applies(e)))
                foreach (var item in g.Elements().Where(Applies))
                {
                    var include = Expand((string?)item.Attribute("Include") ?? "");
                    var remove = Expand((string?)item.Attribute("Remove") ?? "");
                    if (item.Attribute("Exclude") is not null)
                        Partial($"Compile/item Exclude expressions require review in {path}.");
                    if (include.Contains("$(") || include.IndexOfAny(['*', '?', ';']) >= 0 || remove.IndexOfAny(['*', '?', ';']) >= 0)
                    {
                        Partial($"Unsupported item expression in {path}.");
                        continue;
                    }
                    if (item.Name.LocalName == "Compile")
                    {
                        if (include.Length > 0)
                            sources.Add(Path.GetFullPath(Path.Combine(dir, include)));
                        if (remove.Length > 0)
                            sources.Remove(Path.GetFullPath(Path.Combine(dir, remove)));
                    }
                    if (item.Name.LocalName == "ProjectReference" && include.Length > 0)
                    {
                        Partial(
                            $"Project reference source merging is approximate; separate assembly identity and per-project framework evaluation require review: {include}."
                        );
                        Visit(Path.Combine(dir, include));
                    }
                    if (item.Name.LocalName == "Reference")
                    {
                        var hint = item.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
                        if (hint is null)
                            Partial($"Unresolved assembly reference {include} in {path}.");
                        else
                        {
                            var r = Path.GetFullPath(Path.Combine(dir, Expand(hint)));
                            Check(r);
                            refs.Add(r);
                            evidence.Append(r).Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(r))));
                        }
                    }
                    if (item.Name.LocalName is "PackageReference" or "Analyzer")
                        Partial($"Package/analyzer/generated source evaluation is not performed for {include} in {path}.");
                }
                foreach (var file in sources)
                {
                    Check(file);
                    var size = new FileInfo(file).Length;
                    if (size > 4 * 1024 * 1024 || (sourceBytes += size) > 64 * 1024 * 1024)
                    {
                        Partial($"Source size limit: {file}");
                        continue;
                    }
                    var content = File.ReadAllBytes(file);
                    if (content.Length > 4 * 1024 * 1024)
                    {
                        Partial($"Source size limit: {file}");
                        continue;
                    }
                    evidence.Append(file).Append(Convert.ToHexString(SHA256.HashData(content)));
                    files[file] = new(file, symbols.ToArray(), language);
                }
                if (root.Attribute("Sdk") is not null || root.Elements().Any(e => e.Name.LocalName is "Import" or "Choose" or "Target"))
                    Partial($"SDK imports, targets and generated sources are not evaluated: {path}.");
                for (var ancestor = new DirectoryInfo(dir); ancestor is not null; ancestor = ancestor.Parent)
                    foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                        if (File.Exists(Path.Combine(ancestor.FullName, name)))
                        {
                            var f = Path.Combine(ancestor.FullName, name);
                            FingerprintImport(f);
                            Partial($"Shared build customization requires trusted evaluation: {f}.");
                        }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
            {
                Partial($"Project context unavailable for {path}: {ex.GetType().Name}.");
            }
        }
        Visit(selection.ProjectPath);
        return new(
            selection,
            files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            refs.ToArray(),
            diagnostics.Distinct().ToArray(),
            diagnostics.Count == 0,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence.ToString()))),
            projectInputs.ToArray()
        );
    }

    internal static void Check(string path)
    {
        if (new Uri(Path.GetFullPath(path)).IsUnc)
            throw new IOException("Local paths required.");
        for (
            FileSystemInfo? e = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
            e is not null;
            e = e is FileInfo f ? f.Directory : ((DirectoryInfo)e).Parent
        )
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are unsupported.");
    }

    internal static CSharpParseOptions ParseOptions(CSharpProjectFile? file) =>
        new(
            LanguageVersionFacts.TryParse(file?.LanguageVersion ?? "latest", out var language) ? language : LanguageVersion.Latest,
            preprocessorSymbols: file?.Defines
        );
}
