using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Schemora.Host;

internal static class BuildStaging
{
    public static string Map(string stage, string original)
    {
        original = Path.GetFullPath(original);
        var volume = Path.GetPathRoot(original)!;
        var name = OperatingSystem.IsWindows() ? volume.TrimEnd('\\', '/').TrimEnd(':') : "root";
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new AppException("BUILD_VOLUME_UNSUPPORTED", "Unsupported source volume.");
        var mapped = Path.GetFullPath(Path.Combine(stage, "checkout", name, Path.GetRelativePath(volume, original)));
        var prefix = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!mapped.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new AppException("BUILD_STAGE_PATH_UNSAFE", "Source cannot be mapped into the private stage.");
        return mapped;
    }

    public static void PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user);
            security.AddAccessRule(
                new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
            new DirectoryInfo(path).Create(security);
        }
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static async Task Copy(BuildPlan plan, string stage, CancellationToken ct)
    {
        PrivateDirectory(stage);
        LocalFiles.CheckNoLinks(stage);
        foreach (var input in plan.Manifest.Inputs.Where(i => i.Kind != "toolchain-reference"))
        {
            ct.ThrowIfCancellationRequested();
            LocalFiles.CheckNoLinks(input.Path);
            var destination = Map(stage, input.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = new FileStream(
                input.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            await using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long length = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0)
            {
                length += read;
                if (length > input.Length)
                    throw Changed();
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (length != input.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != input.Sha256)
                throw Changed();
            AppException Changed() => new("BUILD_INPUT_CHANGED", "Исходный файл изменился перед служебным копированием. Повторите сборку.");
        }
        var originalProps = plan
            .Manifest.Projects.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "Directory.Build.props"))
            .Where(File.Exists)
            .ToHashSet(BuildInputPlanner.Paths);
        string XmlPath(string original) => Map(stage, original) + (originalProps.Contains(original) ? ".schemora-original" : "");
        foreach (var file in plan.XmlFiles)
        {
            var clone = new XDocument(file.Document);
            foreach (var element in clone.Descendants())
            {
                if (element.Parent?.Name.LocalName == "PropertyGroup")
                {
                    var name = element.Name.LocalName;
                    if (name is "OutputPath" or "OutDir" or "BaseOutputPath")
                        element.Value = OutputExpression(stage, "out");
                    else if (name is "BaseIntermediateOutputPath" or "IntermediateOutputPath" or "MSBuildProjectExtensionsPath")
                        element.Value = OutputExpression(stage, "obj");
                    else if (
                        !element.HasElements
                        && Path.IsPathFullyQualified(element.Value)
                        && !element.Value.StartsWith(@"\\", StringComparison.Ordinal)
                    )
                        element.Value = Map(stage, element.Value);
                }
                if (
                    element.Name.LocalName == "add"
                    && element.Parent?.Name.LocalName == "packageSources"
                    && element.Attribute("value") is { } source
                )
                {
                    if (!Uri.TryCreate(source.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        source.Value = Map(stage, Path.GetFullPath(source.Value, Path.GetDirectoryName(file.Path)!));
                }
                if (element.Name.LocalName == "HintPath" && !element.HasElements && Path.IsPathFullyQualified(element.Value))
                    element.Value = Map(stage, element.Value);
                foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "Include" or "Project"))
                {
                    var parts = attribute.Value.Split(';');
                    for (var index = 0; index < parts.Length; index++)
                        if (Path.IsPathFullyQualified(parts[index]))
                            parts[index] = XmlPath(parts[index]);
                    attribute.Value = string.Join(';', parts);
                }
            }
            if (plan.Manifest.Projects.Contains(file.Path, BuildInputPlanner.Paths) && clone.Root is { } root)
            {
                var group = new XElement(
                    root.Name.Namespace + "PropertyGroup",
                    new XElement(root.Name.Namespace + "SchemoraProjectKey", ProjectKey(file.Path)),
                    new XElement(root.Name.Namespace + "OutputPath", OutputExpression(stage, "out")),
                    new XElement(root.Name.Namespace + "OutDir", OutputExpression(stage, "out")),
                    new XElement(root.Name.Namespace + "BaseIntermediateOutputPath", OutputExpression(stage, "obj")),
                    new XElement(root.Name.Namespace + "IntermediateOutputPath", OutputExpression(stage, "obj")),
                    new XElement(root.Name.Namespace + "MSBuildProjectExtensionsPath", OutputExpression(stage, "obj"))
                );
                var finalImport = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Import");
                if (finalImport is not null)
                    finalImport.AddBeforeSelf(group);
                else
                    root.Add(group);
            }
            clone.Save(XmlPath(file.Path), SaveOptions.DisableFormatting);
        }
        foreach (var projectDirectory in plan.Manifest.Projects.Select(p => Path.GetDirectoryName(p)!).Distinct(BuildInputPlanner.Paths))
        {
            string? inherited = null;
            for (var parent = new DirectoryInfo(projectDirectory); parent is not null; parent = parent.Parent)
            {
                var candidate = Path.Combine(parent.FullName, "Directory.Build.props");
                if (File.Exists(candidate))
                {
                    inherited = candidate;
                    break;
                }
            }
            var wrapper = new XElement("Project");
            foreach (
                var project in plan.Manifest.Projects.Where(p => BuildInputPlanner.Paths.Equals(Path.GetDirectoryName(p), projectDirectory))
            )
                wrapper.Add(
                    new XElement(
                        "PropertyGroup",
                        new XAttribute("Condition", "'$(MSBuildProjectFullPath)' == '" + EscapeMsBuild(Map(stage, project)) + "'"),
                        new XElement("SchemoraProjectKey", ProjectKey(project))
                    )
                );
            if (inherited is not null)
                wrapper.Add(new XElement("Import", new XAttribute("Project", XmlPath(inherited))));
            wrapper.Add(
                new XElement(
                    "PropertyGroup",
                    new XElement("BaseIntermediateOutputPath", OutputExpression(stage, "obj")),
                    new XElement("MSBuildProjectExtensionsPath", OutputExpression(stage, "obj")),
                    new XElement("OutputPath", OutputExpression(stage, "out")),
                    new XElement("OutDir", OutputExpression(stage, "out"))
                )
            );
            new XDocument(wrapper).Save(Path.Combine(Map(stage, projectDirectory), "Directory.Build.props"));
        }
        foreach (var sql in plan.SqlFiles)
        {
            var destination = Map(stage, sql.Path);
            var lines = await File.ReadAllLinesAsync(destination, ct);
            foreach (var include in sql.Includes)
                lines[include.Line - 1] = ":r \"" + Map(stage, include.Path) + "\"";
            await File.WriteAllLinesAsync(destination, lines, new UTF8Encoding(false), ct);
        }
    }

    private static string ProjectKey(string project) =>
        Schemora.Workspaces.Identity.Hash(Schemora.Workspaces.Identity.PathKey(project))[..32];

    private static string EscapeMsBuild(string value)
    {
        foreach (var pair in new[] { ("%", "%25"), ("$", "%24"), ("@", "%40"), ("(", "%28"), (")", "%29"), (";", "%3B"), ("'", "%27") })
            value = value.Replace(pair.Item1, pair.Item2, StringComparison.Ordinal);
        return value;
    }

    private static string OutputExpression(string stage, string leaf) =>
        Path.Combine(stage, "b", "$(SchemoraProjectKey)", leaf) + Path.DirectorySeparatorChar;

    public static string OutputDirectory(string stage, string project) => Path.Combine(stage, "b", ProjectKey(project), "out");
}
