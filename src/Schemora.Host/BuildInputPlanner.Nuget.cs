using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Schemora.Host;

public sealed record BuildPackageReference(string ProjectPath, string Id, string Version, string PackagePath);

public sealed partial class BuildInputPlanner
{
    private sealed partial class Session
    {
        private readonly Dictionary<string, string> centralVersions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<BuildPackageReference> packageReferences = [];
        private readonly HashSet<string> inspectedPackages = new(Paths);
        private string? PackageRoot => overrides.GetValueOrDefault("NuGetPackageRoot");

        private void CentralPackage(XElement item, Dictionary<string, string> props, string path, string project)
        {
            PackageMetadata(item, path);
            var id = item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;
            var version = item.Attribute("Version")?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
            if (id is not null && version is not null)
            {
                var expanded = Expand(version, props, path, true) ?? "";
                if (centralVersions.TryGetValue(project + "\0" + id, out var prior) && prior != expanded)
                    Problem("BUILD_PACKAGE_VERSION_CONFLICT", "Conflicting central package versions cannot be verified: " + id);
                centralVersions[project + "\0" + id] = expanded;
            }
        }

        private async Task Package(XElement item, Dictionary<string, string> props, string path, string project)
        {
            PackageMetadata(item, path);
            if (rootSdk != true)
            {
                Problem("BUILD_PACKAGE_LEGACY_UNSUPPORTED", "NuGet database references require an SDK-style SQL project: " + path);
                return;
            }
            var id = Expand(item.Attribute("Include")?.Value ?? "", props, path, true);
            var versionText =
                item.Attribute("VersionOverride")?.Value
                ?? item.Attribute("Version")?.Value
                ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "VersionOverride")?.Value
                ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                ?? (id is not null ? centralVersions.GetValueOrDefault(project + "\0" + id) : null);
            var centrallyManaged =
                props.GetValueOrDefault("ManagePackageVersionsCentrally")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            var hasOverride =
                item.Attribute("VersionOverride") is not null || item.Elements().Any(e => e.Name.LocalName == "VersionOverride");
            var hasVersion = item.Attribute("Version") is not null || item.Elements().Any(e => e.Name.LocalName == "Version");
            if ((!hasVersion && !centrallyManaged) || (hasOverride && !centrallyManaged))
                Problem(
                    "BUILD_PACKAGE_METADATA_UNVERIFIED",
                    "Central/override package versions require explicit ManagePackageVersionsCentrally=true: " + path
                );
            var version = versionText is null ? null : Expand(versionText, props, path, true);
            if (
                id is null
                || version is null
                || !Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant)
                || !Regex.IsMatch(
                    version,
                    @"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[A-Za-z0-9.-]+)?$",
                    RegexOptions.CultureInvariant
                )
            )
            {
                Problem(
                    "BUILD_PACKAGE_VERSION_UNPINNED",
                    "PackageReference needs a literal, pinned version; floating/range/generated versions are not a verified closure: "
                        + path
                );
                return;
            }
            if (PackageRoot is null)
            {
                Problem("BUILD_PACKAGE_CACHE_UNAVAILABLE", "A private package cache is required to verify NuGet DACPAC inputs.");
                return;
            }
            var directory = System.IO.Path.Combine(PackageRoot, id.ToLowerInvariant(), version.ToLowerInvariant());
            var reference = new BuildPackageReference(project, id, version, directory);
            if (
                packageReferences.Any(p =>
                    Paths.Equals(p.ProjectPath, project)
                    && p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                    && !p.Version.Equals(version, StringComparison.OrdinalIgnoreCase)
                )
            )
                Problem("BUILD_PACKAGE_VERSION_CONFLICT", "Conflicting package versions cannot be verified: " + id);
            if (!packageReferences.Contains(reference))
                packageReferences.Add(reference);
            var generatePath =
                item.Attribute("GeneratePathProperty")?.Value
                ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "GeneratePathProperty")?.Value;
            if (
                generatePath?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                || !Directory.Exists(directory)
                || Directory.Exists(System.IO.Path.Combine(directory, "tools"))
            )
                props["Pkg" + id.Replace('.', '_').Replace('-', '_')] = directory;
            props["NuGetPackageRoot"] = PackageRoot;
            if (!Directory.Exists(directory))
            {
                Problem(
                    "BUILD_PACKAGE_RESTORE_REQUIRED",
                    "Pinned NuGet DACPAC package needs restore in the private staging area: " + id + " " + version
                );
                return;
            }
            var hintPath = item.Attribute("HintPath")?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
            if (!string.IsNullOrWhiteSpace(hintPath))
                foreach (var input in await PathsFor(hintPath, props, project, false))
                    await Add(input, Kind(input));
            var dacpacName =
                item.Attribute("DacpacName")?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "DacpacName")?.Value;
            if (dacpacName is not null)
            {
                var expandedName = Expand(dacpacName, props, path, true);
                if (
                    string.IsNullOrWhiteSpace(expandedName)
                    || expandedName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                    || expandedName.Contains('/')
                    || expandedName.Contains('\\')
                )
                    Problem(
                        "BUILD_PACKAGE_METADATA_UNVERIFIED",
                        "DacpacName must be a literal file name; use an explicit HintPath for external references: " + path
                    );
            }
            if (!inspectedPackages.Add(directory))
                return;
            await DirectoryInputs(directory);
            var packageFiles = files.Keys.Where(p => Within(p, directory)).ToArray();
            await VerifyPackageCache(directory, id, version, packageFiles);
            if (!packageFiles.Any(p => p.EndsWith(".dacpac", StringComparison.OrdinalIgnoreCase)))
                Problem("BUILD_PACKAGE_NOT_DACPAC", "Only auditable DACPAC packages are supported: " + id);
            if (
                packageFiles.Any(p =>
                    p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                )
            )
                Problem("BUILD_PACKAGE_CODE_UNVERIFIED", "A package contains executable analysis/build code: " + id);
            foreach (var nuspec in packageFiles.Where(p => p.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)))
            {
                var document = await Xml(nuspec);
                if (document?.Descendants().Any(e => e.Name.LocalName == "dependency") == true)
                    Problem(
                        "BUILD_PACKAGE_DEPENDENCIES_UNVERIFIED",
                        "A package has transitive NuGet dependencies requiring external restore/verification: " + id
                    );
            }
            foreach (
                var import in packageFiles.Where(p =>
                    p.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
                )
            )
                await Evaluate(import, props, project, 0);
        }

        private void PackageMetadata(XElement item, string path)
        {
            foreach (var name in new[] { "Version", "VersionOverride", "GeneratePathProperty", "HintPath", "DacpacName" })
            {
                var elements = item.Elements().Where(e => e.Name.LocalName == name).ToArray();
                if (elements.Any(e => e.Attribute("Condition") is not null || e.HasElements))
                    Problem("BUILD_PACKAGE_METADATA_UNVERIFIED", "Conditional/generated package metadata cannot be verified: " + path);
                var values = elements
                    .Select(e => e.Value)
                    .Concat(item.Attribute(name) is { } attribute ? new[] { attribute.Value } : [])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (values.Length > 1)
                    Problem("BUILD_PACKAGE_VERSION_CONFLICT", "Conflicting package metadata cannot be verified: " + path);
            }
        }

        private async Task VerifyPackageCache(string directory, string id, string version, IReadOnlyList<string> packageFiles)
        {
            var archive = System.IO.Path.Combine(directory, id.ToLowerInvariant() + "." + version.ToLowerInvariant() + ".nupkg");
            var hashPath = archive + ".sha512";
            var marker = System.IO.Path.Combine(directory, ".nupkg.metadata");
            var specs = packageFiles.Where(p => p.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (specs.Length != 1 || !files.ContainsKey(archive) || !files.ContainsKey(hashPath) || !files.ContainsKey(marker))
            {
                Problem("BUILD_PACKAGE_CACHE_UNVERIFIED", "The private package cache has incomplete restore/provenance metadata: " + id);
                return;
            }
            var specification = await Xml(specs[0]);
            var metadata = specification?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            if (
                !string.Equals(
                    metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value,
                    id,
                    StringComparison.OrdinalIgnoreCase
                )
                || !string.Equals(
                    metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value,
                    version,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                Problem("BUILD_PACKAGE_CACHE_UNVERIFIED", "The cached package identity/version does not match PackageReference: " + id);
            if (files[hashPath].Length > 4096 || files[marker].Length > 1024 * 1024)
            {
                Problem("BUILD_PACKAGE_CACHE_UNVERIFIED", "Package restore metadata exceeds its verification bound: " + id);
                return;
            }
            var archiveBytes = await ReadVerified(archive);
            var hashBytes = await ReadVerified(hashPath);
            var markerBytes = await ReadVerified(marker);
            if (archiveBytes is null || hashBytes is null || markerBytes is null)
                return;
            var actualHash = Convert.ToBase64String(SHA512.HashData(archiveBytes));
            if (Encoding.UTF8.GetString(hashBytes).Trim() != actualHash)
                Problem("BUILD_PACKAGE_CACHE_UNVERIFIED", "Cached package SHA512 does not match the package archive: " + id);
            try
            {
                using var document = JsonDocument.Parse(markerBytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    Problem("BUILD_PACKAGE_CACHE_UNVERIFIED", "The package restore marker is invalid: " + id);
            }
            catch (JsonException)
            {
                Problem("BUILD_PACKAGE_CACHE_UNVERIFIED", "The package restore marker is invalid: " + id);
            }
        }

        private async Task NugetConfiguration(string path)
        {
            var document = await Xml(path);
            if (document?.Root is null)
                return;
            foreach (
                var entry in document.Descendants().Where(e => e.Parent?.Name.LocalName == "packageSources" && e.Name.LocalName == "add")
            )
            {
                var value = entry.Attribute("value")?.Value;
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                    continue;
                if (value.Contains('%') || value.Contains("$(", StringComparison.Ordinal))
                {
                    Problem("BUILD_NUGET_SOURCE_UNRESOLVED", "A local NuGet source uses an unresolved environment/property input: " + path);
                    continue;
                }
                try
                {
                    var directory = System.IO.Path.GetFullPath(value, System.IO.Path.GetDirectoryName(path)!);
                    Local(directory);
                    if (Directory.Exists(directory))
                        await DirectoryInputs(directory);
                    else
                        Problem("BUILD_NUGET_SOURCE_MISSING", "A local NuGet feed directory is missing: " + path);
                }
                catch (Exception error) when (Io(error))
                {
                    Problem("BUILD_NUGET_SOURCE_UNRESOLVED", "A local NuGet feed cannot be inspected: " + path);
                }
            }
        }

        private bool MissingPackageInput(string path) =>
            packageReferences.Any(p => !Directory.Exists(p.PackagePath) && Within(path, p.PackagePath));
    }
}
