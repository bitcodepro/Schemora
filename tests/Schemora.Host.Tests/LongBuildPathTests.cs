using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Schemora.Host;
using Schemora.SqlServer;
using Xunit;

namespace Schemora.Host.Tests;

[CollectionDefinition(LongBuildPathCollection.Name, DisableParallelization = true)]
public sealed class LongBuildPathCollection
{
    public const string Name = "Long SQL build paths";
}

[Collection(LongBuildPathCollection.Name)]
public sealed class LongBuildPathTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraLongBuild-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongCustomDataDirectoryBuildUsesShortTemporaryStageAndCleansIt(bool invalidSql)
    {
        if (!OperatingSystem.IsWindows())
            return; // Win32 process CWD limitation is the regression.
        var source = Path.Combine(directory, "source");
        Copy(Path.Combine(FindRepository(), "tests", "Fixtures", "ComplexBuild"), source);
        var project = Path.Combine(source, "Main", "Main.sqlproj");
        using (var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions()))
        {
            model.AddObjects(File.ReadAllText(Path.Combine(source, "references", "External.sql")));
            DacPackageExtensions.BuildPackage(
                Path.Combine(source, "references", "External.dacpac"),
                model,
                new PackageMetadata { Name = "ExternalDatabase", Version = "1.0" }
            );
        }
        if (invalidSql)
            File.WriteAllText(Path.Combine(source, "Main", "Invalid.sql"), "CREATE TABLE dbo.Broken (Id int,); SELECT FROM;");
        var before = State(source);
        var dataDirectory = Path.Combine(directory, "custom-" + new string('a', 65), new string('b', 65), new string('c', 45));
        Assert.True(dataDirectory.Length >= 200);
        var builder = new SqlProjectBuilder(new Toolchain(dataDirectory), new DacpacReader());

        // Observe only the dedicated fallback root. Existing stages belong to other work and are never deleted here.
        var fallbackRoot = Path.Combine(Path.GetTempPath(), "SchemoraBuilds");
        Directory.CreateDirectory(fallbackRoot);
        var created = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        using var watcher = new FileSystemWatcher(fallbackRoot)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
        };
        var ownedSourceSegment = Path.DirectorySeparatorChar + Path.GetFileName(directory) + Path.DirectorySeparatorChar;
        watcher.Created += (sender, change) =>
        {
            // Other test processes may be building concurrently. Match this fixture's unique source
            // directory inside its copied closure before associating a temporary GUID with this test.
            if (!(change.FullPath + Path.DirectorySeparatorChar).Contains(ownedSourceSegment, StringComparison.OrdinalIgnoreCase))
                return;
            var relative = Path.GetRelativePath(fallbackRoot, change.FullPath);
            var stageName = relative.Split(Path.DirectorySeparatorChar)[0];
            if (Guid.TryParseExact(stageName, "N", out _))
                created.TryAdd(Path.Combine(fallbackRoot, stageName), 0);
        };
        watcher.EnableRaisingEvents = true;

        if (invalidSql)
        {
            var error = await Assert.ThrowsAsync<AppException>(() => builder.BuildWithManifest(project, true, default));
            Assert.Equal("SQL_BUILD_FAILED", error.Code);
            var manifests = Path.Combine(dataDirectory, "build-manifests");
            Assert.True(!Directory.Exists(manifests) || !Directory.EnumerateFiles(manifests).Any());
        }
        else
        {
            var result = await builder.BuildWithManifest(project, true, default);
            Assert.Contains(result.Graph.Tables, table => table.Name == "MainItems");
            Assert.True(result.Manifest.Complete);
            Assert.NotNull(result.Manifest.Toolchain);
            Assert.NotEmpty(result.Manifest.Toolchain.MsBuildVersion);
            Assert.False(
                (
                    await builder.CheckManifest(
                        result.Manifest with
                        {
                            Toolchain = result.Manifest.Toolchain with { MsBuildVersion = "changed" },
                        }
                    )
                ).Current
            );
            if (result.Manifest.Toolchain.Kind == "SDK-style")
            {
                Assert.NotNull(result.Manifest.Toolchain.SdkVersion);
                Assert.NotNull(result.Manifest.Toolchain.SqlProjectSdk);
            }
            else
            {
                Assert.NotNull(result.Manifest.Toolchain.SsdtTargetsSha256);
                Assert.NotNull(result.Manifest.Toolchain.SsdtTaskVersion);
            }
            var manifestPath = Path.Combine(dataDirectory, "build-manifests", result.Manifest.Id + ".json");
            Assert.True(File.Exists(manifestPath)); // Metadata stays in the requested data directory.
            var stored = builder.ReadManifest(result.Manifest.Id);
            Assert.NotNull(stored);
            Assert.Equal(result.Manifest.Fingerprint, stored.Fingerprint);
            Assert.True((await builder.CheckManifest(stored)).Current);
        }

        // FileSystemWatcher dispatch is asynchronous; avoid depending on callback scheduling at process exit.
        for (var attempt = 0; created.IsEmpty && attempt < 20; attempt++)
            await Task.Delay(50);
        Assert.NotEmpty(created);
        Assert.All(created.Keys, path => Assert.False(Directory.Exists(path), "Fallback stage was not removed: " + path));
        var regularBuilds = Path.Combine(dataDirectory, "builds");
        Assert.True(!Directory.Exists(regularBuilds) || !Directory.EnumerateDirectories(regularBuilds).Any());
        Assert.Equal(before, State(source)); // Success and failure must both leave the original closure untouched.
    }

    [LegacySsdtFact]
    public async Task DeepLegacySourceBuildUsesShortIntermediateOutputsAndPreservesItsClosure()
    {
        // The source remains fully mirrored. Standard SSDT/.NET Framework generated filenames
        // used to exceed MAX_PATH even though the process working directory itself was short enough.
        var source = Path.Combine(
            directory,
            "source",
            "clean-depth-" + new string('d', Math.Max(18, 110 - Path.Combine(directory, "source", "clean-depth-", "Legacy").Length)),
            "Legacy"
        );
        Copy(Path.Combine(FindRepository(), "tests", "Fixtures", "Legacy"), source);
        Assert.True(source.Length >= 110);
        var project = Path.Combine(source, "Legacy.sqlproj");
        var before = State(source);
        var dataDirectory = Path.Combine(directory, "custom-" + new string('a', 65), new string('b', 65), new string('c', 45));
        Assert.True(dataDirectory.Length >= 200);
        var toolchain = new Toolchain(dataDirectory);
        Assert.True((await toolchain.FindLegacy(default)).Ready);
        var builder = new SqlProjectBuilder(toolchain, new DacpacReader());

        var fallbackRoot = Path.Combine(Path.GetTempPath(), "SchemoraBuilds");
        Directory.CreateDirectory(fallbackRoot);
        var created = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var ownedSourceSegment = Path.DirectorySeparatorChar + Path.GetFileName(directory) + Path.DirectorySeparatorChar;
        using var watcher = new FileSystemWatcher(fallbackRoot)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (sender, change) =>
        {
            if (!(change.FullPath + Path.DirectorySeparatorChar).Contains(ownedSourceSegment, StringComparison.OrdinalIgnoreCase))
                return;
            var stageName = Path.GetRelativePath(fallbackRoot, change.FullPath).Split(Path.DirectorySeparatorChar)[0];
            if (Guid.TryParseExact(stageName, "N", out _))
                created.TryAdd(Path.Combine(fallbackRoot, stageName), 0);
        };
        watcher.EnableRaisingEvents = true;

        var result = await builder.BuildWithManifest(project, true, default);
        Assert.Contains(result.Graph.Tables, table => table.Name == "LegacyItems");
        Assert.True(result.Manifest.Complete);
        Assert.True(File.Exists(Path.Combine(dataDirectory, "build-manifests", result.Manifest.Id + ".json")));
        var stored = builder.ReadManifest(result.Manifest.Id);
        Assert.NotNull(stored);
        Assert.True((await builder.CheckManifest(stored)).Current);
        Assert.Equal(before, State(source));
        for (var attempt = 0; created.IsEmpty && attempt < 20; attempt++)
            await Task.Delay(50);
        Assert.NotEmpty(created);
        Assert.All(created.Keys, path => Assert.False(Directory.Exists(path), "Legacy fallback stage was not removed: " + path));
        var regularBuilds = Path.Combine(dataDirectory, "builds");
        Assert.True(!Directory.Exists(regularBuilds) || !Directory.EnumerateDirectories(regularBuilds).Any());
    }

    private static string State(string path) =>
        string.Join(
            "\n",
            Directory
                .GetFiles(path, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
        );

    private static string FindRepository()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Schemora.slnx")))
                return current.FullName;
        throw new InvalidOperationException("Repository fixtures were not found.");
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var child in Directory.GetDirectories(from))
            Copy(child, Path.Combine(to, Path.GetFileName(child)));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (
            !full.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("SchemoraLongBuild-", StringComparison.Ordinal)
        )
            throw new InvalidOperationException("Test cleanup escaped the owned temporary directory.");
        if (Directory.Exists(full))
            Directory.Delete(full, true);
    }
}

/// <summary>Report an explicit skipped test where the optional legacy compiler is unavailable.</summary>
public sealed class LegacySsdtFactAttribute : FactAttribute
{
    public LegacySsdtFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Legacy SSDT requires Windows and a compatible Visual Studio MSBuild installation.";
            return;
        }
        var tools = new Toolchain(Path.GetTempPath()).FindLegacy(CancellationToken.None).GetAwaiter().GetResult();
        if (!tools.Ready)
            Skip = "Compatible Visual Studio MSBuild and SSDT targets are not installed.";
    }
}
