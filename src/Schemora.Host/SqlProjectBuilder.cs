using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed class SqlProjectBuilder(Toolchain toolchain, DacpacReader reader)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly BuildInputPlanner planner = new();
    private string ManifestDirectory => Path.Combine(toolchain.DataDirectory, "build-manifests");

    // A private DOTNET_CLI_HOME makes every data directory a first run; without these switches the SDK appends <home>\.dotnet\tools to the user's persistent PATH each time.
    internal static Dictionary<string, string> DotnetEnvironment(string dataDirectory, string packages) =>
        new()
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(dataDirectory, "dotnet-home"),
            ["NUGET_PACKAGES"] = packages,
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };

    public async Task<BuildInputManifest> Plan(string? input, CancellationToken ct = default)
    {
        var path = LocalFiles.Validate(input, ".sqlproj");
        var context = await Context(path, false, ct);
        return (await planner.Create(path, ct, context.Properties)).Manifest;
    }

    public BuildInputManifest? ReadManifest(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            return null;
        var path = Path.Combine(ManifestDirectory, id + ".json");
        if (!File.Exists(path))
            return null;
        LocalFiles.CheckNoLinks(path);
        if (new FileInfo(path).Length > 32L * 1024 * 1024)
            throw new AppException("BUILD_MANIFEST_TOO_LARGE", "Manifest exceeds the metadata limit.");
        return JsonSerializer.Deserialize<BuildInputManifest>(File.ReadAllText(path), CatalogStore.Json);
    }

    public async Task<BuildManifestCheck> CheckManifest(BuildInputManifest manifest, CancellationToken ct = default)
    {
        if (!manifest.Complete)
            return new(false, manifest.Diagnostics);
        try
        {
            var current = await Plan(manifest.ProjectPath, ct);
            if (manifest.Toolchain is { } recorded)
            {
                var context = await Context(manifest.ProjectPath, true, ct);
                var actual = await BuildToolCapture.Capture(
                    context.Executable!,
                    context.Sdk,
                    context.Legacy?.SsdtTargets,
                    Path.GetDirectoryName(manifest.ProjectPath)!,
                    ct,
                    context.SqlProjectSdk
                );
                if (JsonSerializer.Serialize(recorded, CatalogStore.Json) != JsonSerializer.Serialize(actual, CatalogStore.Json))
                    return new(
                        false,
                        [new("warning", "BUILD_TOOLCHAIN_CHANGED", "The recorded build toolchain changed; rebuild is required.")]
                    );
            }
            return new(current.Complete && current.Fingerprint == manifest.Fingerprint, current.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(
                false,
                [new("warning", "BUILD_MANIFEST_INPUT_UNAVAILABLE", "Source closure cannot be verified; refresh/rebuild is required.")]
            );
        }
    }

    public async Task<SchemaGraph> Build(string? input, bool trustProject, CancellationToken ct) =>
        (await BuildWithManifest(input, trustProject, ct)).Graph;

    public async Task<SqlProjectBuildResult> BuildWithManifest(string? input, bool trustProject, CancellationToken ct)
    {
        if (!trustProject)
            throw new AppException(
                "PROJECT_TRUST_REQUIRED",
                "Building executes the project's MSBuild targets. Confirm that you trust the project before starting.",
                403
            );
        var path = LocalFiles.Validate(input, ".sqlproj");
        var context = await Context(path, true, ct);
        var sourceDirectory = Path.GetDirectoryName(path)!;
        if (Within(toolchain.DataDirectory, sourceDirectory))
            throw new AppException("BUILD_DATA_INSIDE_PROJECT", "The application data directory must be outside the project being built.");
        if (!await gate.WaitAsync(0, ct))
            throw new AppException("BUILD_BUSY", "Another build is already running. Wait for it to finish.", 409);
        var dataBuilds = Path.Combine(toolchain.DataDirectory, "builds");
        var builds = dataBuilds;
        var stage = Path.Combine(builds, Guid.NewGuid().ToString("N"));
        try
        {
            // CreateProcess cannot use a current directory beyond MAX_PATH, even when .NET file APIs
            // support it. A private short temporary root preserves the mirrored source hierarchy.
            if (OperatingSystem.IsWindows() && (stage.Length > 100 || BuildStaging.Map(stage, sourceDirectory).Length > 240))
            {
                Directory.CreateDirectory(dataBuilds);
                LocalFiles.CheckNoLinks(dataBuilds);
                builds = Path.Combine(Path.GetTempPath(), "SchemoraBuilds");
                if (Within(builds, sourceDirectory))
                    throw new AppException(
                        "BUILD_STAGE_INSIDE_PROJECT",
                        "Could not choose a short staging path outside the project; move the project or use a DACPAC.",
                        422
                    );
                stage = Path.Combine(builds, Guid.NewGuid().ToString("N"));
                if (BuildStaging.Map(stage, sourceDirectory).Length > 240)
                    throw new AppException(
                        "BUILD_PATH_TOO_LONG",
                        "The project path is too long for Windows tooling. Move the project closer to the drive root or import an existing DACPAC.",
                        422
                    );
            }
            var plan = await planner.Create(path, ct, context.Properties);
            if (
                context.Sdk
                && plan.Manifest.Diagnostics.Count > 0
                && plan.Manifest.Diagnostics.All(d => d.Code == "BUILD_PACKAGE_RESTORE_REQUIRED")
            )
            {
                Directory.CreateDirectory(builds);
                LocalFiles.CheckNoLinks(builds);
                var restoreStage = Path.Combine(stage, "restore");
                await BuildStaging.Copy(plan, restoreStage, ct);
                var restoreProject = BuildStaging.Map(restoreStage, path);
                var restoreCache = context.Properties["NuGetPackageRoot"];
                var restore = await ProcessRunner.Run(
                    context.Executable!,
                    ["restore", restoreProject, "--packages", restoreCache, "--nologo", "--verbosity", "minimal", "-nodeReuse:false"],
                    Path.GetDirectoryName(restoreProject)!,
                    TimeSpan.FromMinutes(5),
                    ct,
                    DotnetEnvironment(toolchain.DataDirectory, restoreCache)
                );
                if (restore.ExitCode != 0)
                    throw new AppException(
                        "BUILD_PACKAGE_RESTORE_FAILED",
                        "Could not restore pinned NuGet DACPAC packages in the staging copy.",
                        422,
                        restore.Output
                    );
                LocalFiles.CheckNoLinks(restoreStage);
                Directory.Delete(restoreStage, true);
                plan = await planner.Create(path, ct, context.Properties);
            }
            if (!plan.Manifest.Complete)
                throw new AppException(
                    "BUILD_INPUTS_INCOMPLETE",
                    "Could not verify the complete portable set of source inputs. Check the diagnostics; for dynamic imports/tasks, build externally and import the DACPAC.",
                    422,
                    JsonSerializer.Serialize(plan.Manifest.Diagnostics, CatalogStore.Json)
                );
            if (plan.Manifest.Projects.Any(project => Within(toolchain.DataDirectory, Path.GetDirectoryName(project)!)))
                throw new AppException("BUILD_DATA_INSIDE_PROJECT", "The data directory is inside one of the dependency projects.");
            Directory.CreateDirectory(builds);
            LocalFiles.CheckNoLinks(builds);
            await BuildStaging.Copy(plan, stage, ct);
            var project = BuildStaging.Map(stage, path);
            var projectDirectory = Path.GetDirectoryName(project)!;
            var packages =
                (plan.Manifest.Packages?.Count ?? 0) > 0
                    ? BuildStaging.Map(stage, context.Properties["NuGetPackageRoot"])
                    : context.Properties["NuGetPackageRoot"];
            Directory.CreateDirectory(packages);
            var arguments = context.Sdk
                ? new List<string>
                {
                    "build",
                    project,
                    "-m:1",
                    "--configuration",
                    "Release",
                    "--nologo",
                    "--verbosity",
                    "minimal",
                    "-nodeReuse:false",
                    "-p:Platform=AnyCPU",
                }
                : new List<string>
                {
                    project,
                    "/m:1",
                    "/restore",
                    "/t:Build",
                    "/p:Configuration=Release",
                    "/p:Platform=AnyCPU",
                    "/nologo",
                    "/v:minimal",
                    "/nodeReuse:false",
                    "/p:SQLDBExtensionsRefPath=" + Path.GetDirectoryName(context.Legacy!.SsdtTargets),
                };
            var prefix = context.Sdk ? "-p:" : "/p:";
            arguments.Add(prefix + "RestorePackagesPath=" + packages);
            if (context.Legacy is not null)
                arguments.Add("/p:VisualStudioVersion=" + context.Properties["VisualStudioVersion"]);
            var selectedTools = await BuildToolCapture.Capture(
                context.Executable!,
                context.Sdk,
                context.Legacy?.SsdtTargets,
                projectDirectory,
                ct,
                context.SqlProjectSdk
            );
            plan = plan with { Manifest = plan.Manifest with { Toolchain = selectedTools } };
            var output = await ProcessRunner.Run(
                context.Executable!,
                arguments,
                projectDirectory,
                TimeSpan.FromMinutes(5),
                ct,
                DotnetEnvironment(toolchain.DataDirectory, packages)
            );
            if (output.ExitCode != 0)
                throw new AppException(
                    "SQL_BUILD_FAILED",
                    "The SQL project build failed. Check dependencies and tool diagnostics; you can import an existing DACPAC.",
                    422,
                    output.Output
                );
            var outputDirectory = BuildStaging.OutputDirectory(stage, path);
            var artifacts = Directory.Exists(outputDirectory)
                ? Directory.EnumerateFiles(outputDirectory, "*.dacpac", SearchOption.TopDirectoryOnly).ToArray()
                : [];
            var named = artifacts
                .Where(file =>
                    Path.GetFileNameWithoutExtension(file)
                        .Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
                )
                .ToArray();
            var artifact =
                named.Length == 1 ? named[0]
                : artifacts.Length == 1 ? artifacts[0]
                : null;
            if (artifact is null)
                throw new AppException(
                    "DACPAC_OUTPUT_AMBIGUOUS",
                    "The build finished, but the root project's output DACPAC could not be identified unambiguously.",
                    422
                );
            var graph = reader.Read(artifact) with { SourcePath = path };
            var check = await CheckManifest(plan.Manifest, ct);
            if (!check.Current)
                throw new AppException(
                    "BUILD_INPUT_CHANGED",
                    "The source dependency closure changed during the build; the result was not published. Run Rebuild again.",
                    409
                );
            Directory.CreateDirectory(ManifestDirectory);
            LocalFiles.CheckNoLinks(ManifestDirectory);
            var manifestPath = Path.Combine(ManifestDirectory, plan.Manifest.Id + ".json");
            await using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, plan.Manifest, CatalogStore.Json, ct);
            graph = graph with
            {
                Diagnostics = graph
                    .Diagnostics.Append(
                        new(
                            "info",
                            "BUILD_SOURCE_CLOSURE_VERIFIED",
                            $"Verified source closure: {plan.Manifest.Inputs.Count} files / {plan.Manifest.Projects.Count} SQL projects; manifest {plan.Manifest.Id}."
                        )
                    )
                    .ToArray(),
            };
            return new(graph, plan.Manifest);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage) && Path.GetDirectoryName(Path.GetFullPath(stage)) == Path.GetFullPath(builds))
                {
                    LocalFiles.CheckNoLinks(stage);
                    Directory.Delete(stage, true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            gate.Release();
        }
    }

    private sealed record ToolContext(
        bool Sdk,
        string? Executable,
        LegacyTools? Legacy,
        IReadOnlyDictionary<string, string> Properties,
        string? SqlProjectSdk
    );

    private async Task<ToolContext> Context(string path, bool requireTools, CancellationToken ct)
    {
        using var xml = XmlReader.Create(
            path,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4L * 1024 * 1024,
            }
        );
        var document = XDocument.Load(xml);
        var root = document.Root ?? throw new AppException("PROJECT_INVALID", "SQL project XML has no root.");
        var sdk = root.Attribute("Sdk")?.Value ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value;
        var isSdk = sdk?.Split('/')[0].Equals("Microsoft.Build.Sql", StringComparison.OrdinalIgnoreCase) == true;
        var legacy = root.Descendants()
            .Any(e =>
                e.Name.LocalName == "Import"
                && e.Attribute("Project")
                    ?.Value.Contains("Microsoft.Data.Tools.Schema.SqlTasks.targets", StringComparison.OrdinalIgnoreCase) == true
            );
        if (!isSdk && !legacy)
            throw new AppException(
                "PROJECT_FORMAT_UNSUPPORTED",
                "Microsoft.Build.Sql SDK-style and legacy SSDT SQL projects are supported."
            );
        LegacyTools? pair = null;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var executable = Toolchain.Dotnet;
        if (!isSdk)
        {
            pair = await toolchain.FindLegacy(ct);
            executable = pair.MsBuild;
            if (pair.Ready)
            {
                properties["SQLDBExtensionsRefPath"] = Path.GetDirectoryName(pair.SsdtTargets)!;
                properties["VisualStudioVersion"] = Directory.GetParent(Path.GetDirectoryName(pair.SsdtTargets!)!)!.Name.TrimStart('v');
            }
            else if (requireTools)
                throw new AppException(
                    "SSDT_TOOLCHAIN_MISSING",
                    "Compatible Visual Studio MSBuild and SSDT targets are required; you can import an existing DACPAC instead.",
                    422
                );
        }
        else if (executable is null && requireTools)
            throw new AppException("DOTNET_SDK_MISSING", ".NET SDK not found. Install the SDK or import an existing DACPAC.", 422);
        if (executable is not null)
            properties["SchemoraBuildExecutable"] = executable;
        properties["NuGetPackageRoot"] = Path.Combine(toolchain.DataDirectory, "nuget-packages") + Path.DirectorySeparatorChar;
        var sqlSdk = isSdk
            ? (
                root.Attribute("Sdk")?.Value
                ?? sdk + "/" + root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Version")?.Value
            )
            : null;
        return new(isSdk, executable, pair, properties, sqlSdk);
    }

    private static bool Within(string file, string directory)
    {
        var relative = Path.GetRelativePath(directory, Path.GetFullPath(file));
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
