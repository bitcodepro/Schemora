using Schemora.Workspaces.Discovery;
using Xunit;
using Xunit.Abstractions;

namespace Schemora.Workspaces.Tests;

public sealed class DiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SeparatesRootsAndSameNamedProjectsAndFindsNestedGitMarkers()
    {
        using var tree = new TestTree();
        var first = tree.Directory("first");
        var second = tree.Directory("second");
        tree.Directory("first/.git");
        tree.File("first/.git/hidden.sql", "not a source");
        tree.File("first/nested/.git", "gitdir: /outside/not-read");
        tree.File("first/db/Commerce.sqlproj", "<Project />");
        tree.File("second/db/Commerce.sqlproj", "<Project />");
        tree.File("first/App.slnx", "<Solution />");
        tree.File("first/App.csproj", "<Project />");
        tree.File("first/Entity.cs", "class Entity {}");
        tree.File("first/appsettings.Development.json", "{\"Password\":\"never persist me\"}");
        var scanner = new WorkspaceDiscovery();
        var result = await scanner.Scan([first, second, first], [], null, default);
        Assert.Equal(2, result.Sources.Count(x => x.Kind == "repository"));
        var projects = result.Sources.Where(x => x.Kind == "sqlProject").ToArray();
        Assert.Equal(2, projects.Length);
        Assert.NotEqual(projects[0].Id, projects[1].Id);
        Assert.Contains(result.Sources, x => x.Kind == "solution");
        Assert.Contains(result.Sources, x => x.Kind == "csharpProject");
        Assert.Contains(result.Sources, x => x.Kind == "csharp");
        Assert.Contains(result.Sources, x => x.Kind == "config");
        Assert.DoesNotContain(result.Sources, x => x.Path.EndsWith("hidden.sql"));
        Assert.Equal(result.Sources.Count, result.Sources.Select(x => x.Id).Distinct().Count());
        var again = await scanner.Scan([second, first], [], null, default);
        Assert.Equal(result.Sources.Select(x => x.Id), again.Sources.Select(x => x.Id));
    }

    [Fact]
    public async Task NeverIndexesBuildArtifactsOrGitInternals()
    {
        using var tree = new TestTree();
        foreach (var directory in new[] { ".git", "bin", "obj", "node_modules", ".vs", "dist", "work", "artifacts" })
            tree.File($"{directory}/not-source.sql", "SELECT 1;");
        tree.File("schema/keep.sql", "CREATE TABLE dbo.Keep(Id int);");
        tree.File("notes.txt", "not indexed");
        var result = await new WorkspaceDiscovery().Scan([tree.Root], [], null, default);
        Assert.Single(result.Sources, x => x.Kind == "sql");
        Assert.Equal(1, result.HashedFiles);
    }

    [Fact]
    public async Task HonorsAnchoredNestedNegationsAndIgnoredParentDirectories()
    {
        using var tree = new TestTree();
        tree.File(".gitignore", "# comment\n*.sql\n!important.sql\n/root-only.cs\ncache/\n!cache/keep.sql\n/generated/*.cs\n");
        tree.File("ignored.sql", "ignored");
        tree.File("important.sql", "kept");
        tree.File("root-only.cs", "ignored");
        tree.File("nested/root-only.cs", "kept");
        tree.File("cache/keep.sql", "ignored because parent is ignored");
        tree.File("generated/skip.cs", "ignored");
        tree.File("nested/generated/keep.cs", "kept");
        tree.File("nested/rescue.sql", "kept by nested rule");
        tree.File("nested/.gitignore", "!rescue.sql\nhidden/\n");
        tree.File("nested/hidden/skip.cs", "ignored");
        var result = await new WorkspaceDiscovery().Scan([tree.Root], [], null, default);
        Assert.Equal(
            new[] { "important.sql", "nested/generated/keep.cs", "nested/rescue.sql", "nested/root-only.cs" },
            Relative(tree, result)
        );
    }

    [Fact]
    public async Task SupportsDoubleStarQuestionBracketEscapesAndUserOverrides()
    {
        using var tree = new TestTree();
        tree.File(
            ".gitignore",
            "**/generated/**\nfile?.sql\nrange[0-3].sql\n\\#secret.sql\n\\!secret.sql\nspace\\ .sql\ntrailing.sql   \n"
        );
        foreach (
            var path in new[]
            {
                "generated/out.sql",
                "nested/generated/out.sql",
                "fileA.sql",
                "range2.sql",
                "#secret.sql",
                "!secret.sql",
                "space .sql",
                "trailing.sql",
                "skip.tmp.sql",
            }
        )
            tree.File(path, "ignored");
        foreach (var path in new[] { "generated/keep.sql", "nested/generated/keep.sql", "fileAB.sql", "range4.sql", "keep.tmp.sql" })
            tree.File(path, "kept");
        var result = await new WorkspaceDiscovery().Scan(
            [tree.Root],
            ["!**/generated/keep.sql", "**/*.tmp.sql", "!keep.tmp.sql"],
            null,
            default
        );
        Assert.Equal(
            new[] { "fileAB.sql", "generated/keep.sql", "keep.tmp.sql", "nested/generated/keep.sql", "range4.sql" },
            Relative(tree, result)
        );
    }

    [Fact]
    public async Task ReusesUnchangedHashesAndTracksChangesAdditionsAndRemovals()
    {
        using var tree = new TestTree();
        var changed = tree.File("A.sql", "CREATE TABLE dbo.A(Id int);");
        var removed = tree.File("B.sql", "CREATE TABLE dbo.B(Id int);");
        tree.File("appsettings.json", "{\"Password\":\"secret\"}");
        var scanner = new WorkspaceDiscovery();
        var first = await scanner.Scan([tree.Root], [], null, default);
        Assert.Equal(2, first.HashedFiles);
        var prior = first.Sources.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var unchanged = await scanner.Scan([tree.Root], [], prior, default);
        Assert.Equal(0, unchanged.HashedFiles);
        Assert.Equal(3, unchanged.ReusedFiles);
        var old = prior[changed];
        System.IO.File.WriteAllText(changed, "CREATE TABLE dbo.Changed(Id bigint);");
        System.IO.File.SetLastWriteTimeUtc(changed, old.LastWrite.UtcDateTime.AddSeconds(2));
        System.IO.File.Delete(removed);
        tree.File("C.sql", "CREATE TABLE dbo.C(Id int);");
        var refreshed = await scanner.Scan([tree.Root], [], prior, default);
        Assert.Equal(2, refreshed.HashedFiles);
        Assert.Equal(1, refreshed.ReusedFiles);
        Assert.DoesNotContain(refreshed.Sources, x => x.Path == removed);
        var updated = Assert.Single(refreshed.Sources, x => x.Path == changed);
        Assert.Equal(old.Id, updated.Id);
        Assert.NotEqual(old.Hash, updated.Hash);
        Assert.Contains(refreshed.Sources, x => x.Path.EndsWith("C.sql"));
    }

    [Fact]
    public async Task LengthInvalidatesCacheAndFullScanDetectsPreservedMetadataEdits()
    {
        using var tree = new TestTree();
        var path = tree.File("A.sql", "one");
        var scanner = new WorkspaceDiscovery();
        var first = await scanner.Scan([tree.Root], [], null, default);
        var old = Assert.Single(first.Sources);
        System.IO.File.WriteAllText(path, "longer");
        System.IO.File.SetLastWriteTimeUtc(path, old.LastWrite.UtcDateTime);
        var resized = await scanner.Scan([tree.Root], [], first.Sources.ToDictionary(x => x.Id), default);
        Assert.Equal(1, resized.HashedFiles);
        var before = Assert.Single(resized.Sources);
        System.IO.File.WriteAllText(path, "edited"); // Same length, same timestamp: full rescan is authoritative.
        System.IO.File.SetLastWriteTimeUtc(path, before.LastWrite.UtcDateTime);
        var full = await scanner.Scan([tree.Root], [], null, default);
        Assert.Equal(1, full.HashedFiles);
        Assert.NotEqual(before.Hash, Assert.Single(full.Sources).Hash);
    }

    [Fact]
    public async Task DoesNotReadConfigContentsAndDoesNotPersistSecrets()
    {
        using var tree = new TestTree();
        var path = tree.File("appsettings.json", "{\"ConnectionStrings\":{\"Db\":\"Password=secret-do-not-read\"}}");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await new WorkspaceDiscovery().Scan([tree.Root], [], null, default);
        var config = Assert.Single(result.Sources);
        Assert.Equal("config", config.Kind);
        Assert.Equal(0, result.HashedFiles);
        Assert.DoesNotContain("secret", config.Hash);
        Assert.Contains(config.Diagnostics, x => x.Contains("contents were not read"));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task SkipsOversizedSourcesWithDiagnostic()
    {
        using var tree = new TestTree();
        var path = tree.File("huge.sql", "");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
            file.SetLength(WorkspaceDiscovery.MaximumFileLength + 1);
        var result = await new WorkspaceDiscovery().Scan([tree.Root], [], null, default);
        Assert.Empty(result.Sources);
        Assert.Contains(result.Diagnostics, x => x.Contains("32 MiB"));
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task HonorsCancellationBeforeAnyTraversal()
    {
        using var tree = new TestTree();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkspaceDiscovery().Scan([tree.Root], [], null, cancellation.Token)
        );
    }

    [Fact]
    public async Task SkipsSymbolicLinksIncludingCyclesWhenSupported()
    {
        using var tree = new TestTree();
        var root = tree.Directory("sources");
        var external = tree.File("outside/secret.sql", "outside");
        tree.File("sources/keep.sql", "kept");
        try
        {
            System.IO.Directory.CreateSymbolicLink(System.IO.Path.Combine(root, "cycle"), root);
            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(root, "linked.sql"), external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            output.WriteLine($"Symbolic-link creation unavailable in this environment: {ex.GetType().Name}");
            return;
        }
        var result = await new WorkspaceDiscovery().Scan([root], [], null, default);
        Assert.Single(result.Sources);
        Assert.EndsWith("keep.sql", result.Sources[0].Path);
        Assert.Equal(2, result.Diagnostics.Count(x => x.Contains("Skipped link")));
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task DiagnosesUnsupportedIgnoreSyntaxAndMissingRoots()
    {
        using var tree = new TestTree();
        tree.File(".gitignore", "[[:digit:]].sql\n");
        tree.File("1.sql", "kept because unsupported rule is diagnosed");
        var result = await new WorkspaceDiscovery().Scan([tree.Root, System.IO.Path.Combine(tree.Root, "missing")], [], null, default);
        Assert.Single(result.Sources);
        Assert.Contains(result.Diagnostics, x => x.Contains("POSIX bracket"));
        Assert.Contains(result.Diagnostics, x => x.Contains("does not exist"));
    }

    [Fact]
    public async Task DirectoryNegationDoesNotUndoAnIndependentFileIgnoreRule()
    {
        using var tree = new TestTree();
        tree.File(".gitignore", "*.sql\n!subdir/\n");
        tree.File("subdir/ignored.sql", "ignored by *.sql despite the directory negation");
        tree.File("subdir/kept.cs", "class Kept {}");
        var result = await new WorkspaceDiscovery().Scan([tree.Root], [], null, default);
        Assert.Equal(new[] { "subdir/kept.cs" }, Relative(tree, result));
    }

    [Fact]
    public async Task MissingRootIsIncompleteInsteadOfAuthoritativeDeletion()
    {
        using var tree = new TestTree();
        var root = tree.Directory("repository");
        tree.File("repository/schema.sql", "CREATE TABLE dbo.Alive(Id int);");
        var scanner = new WorkspaceDiscovery();
        var first = await scanner.Scan([root], [], null, default);
        Assert.True(first.Complete);
        var hidden = System.IO.Path.Combine(tree.Root, "moved-repository");
        System.IO.Directory.Move(root, hidden);
        var missing = await scanner.Scan([root], [], first.Sources.ToDictionary(x => x.Path), default);
        Assert.False(missing.Complete);
        Assert.Empty(missing.Sources);
        Assert.Contains(missing.Diagnostics, x => x.Contains(root) && x.Contains("does not exist"));
    }

    [Fact]
    public async Task UnreadableSourceMakesDiscoveryIncomplete()
    {
        using var tree = new TestTree();
        var path = tree.File("schema.sql", "CREATE TABLE dbo.Locked(Id int);");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await new WorkspaceDiscovery().Scan([tree.Root], [], null, default);
        Assert.False(result.Complete);
        Assert.Empty(result.Sources);
        Assert.Contains(result.Diagnostics, x => x.Contains(path) && x.Contains("Cannot inspect source"));
    }

    [Fact]
    public async Task UnsupportedIgnoreSyntaxIsVisibleButDoesNotInvalidateTraversalCompleteness()
    {
        using var tree = new TestTree();
        tree.File(".gitignore", "[[:digit:]].sql\n");
        tree.File("1.sql", "source");
        var result = await new WorkspaceDiscovery().Scan([tree.Root], [], null, default);
        Assert.True(result.Complete);
        Assert.Single(result.Sources);
        Assert.Contains(result.Diagnostics, x => x.Contains("POSIX bracket"));
    }

    [Fact]
    public async Task FingerprintsBuildConfigurationsIncludingParentsOutsideRootsWithoutStoringContents()
    {
        using var tree = new TestTree();
        var project = tree.File("workspace/db/Commerce.sqlproj", "<Project />");
        var props = tree.File(
            "Directory.Build.props",
            "<Project><PropertyGroup><Secret>PRIVATE_NUGET_VALUE_8ca1</Secret></PropertyGroup></Project>"
        );
        var global = tree.File("global.json", "{\"sdk\":{\"version\":\"10.0.402\"}}");
        var targets = tree.File("workspace/db/custom.targets", "<Project />");
        tree.File("workspace/db/packages.lock.json", "{}");
        tree.File("workspace/appsettings.json", "{\"Password\":\"NEVER_READ_APPLICATION_CONFIG\"}");
        var scanner = new WorkspaceDiscovery();
        var first = await scanner.Scan([System.IO.Path.Combine(tree.Root, "workspace")], [], null, default);
        Assert.True(first.Complete);
        foreach (var path in new[] { props, global, targets })
            Assert.Contains(first.Sources, s => s.Path == path && s.Kind == "buildConfig" && s.Hash.Length == 64);
        Assert.DoesNotContain("PRIVATE_NUGET_VALUE_8ca1", System.Text.Json.JsonSerializer.Serialize(first));
        var old = first.Sources.Single(s => s.Path == props);
        System.IO.File.WriteAllText(props, "<Project><PropertyGroup><Changed>true</Changed></PropertyGroup></Project>");
        System.IO.File.SetLastWriteTimeUtc(props, old.LastWrite.UtcDateTime.AddSeconds(2));
        var second = await scanner.Scan(
            [System.IO.Path.Combine(tree.Root, "workspace")],
            [],
            first.Sources.ToDictionary(s => s.Path),
            default
        );
        Assert.True(second.Complete);
        Assert.NotEqual(old.Hash, second.Sources.Single(s => s.Path == props).Hash);
        System.IO.File.Delete(global);
        var third = await scanner.Scan(
            [System.IO.Path.Combine(tree.Root, "workspace")],
            [],
            second.Sources.ToDictionary(s => s.Path),
            default
        );
        Assert.DoesNotContain(third.Sources, s => s.Path == global);
    }

    [Fact]
    public async Task UnreadableInheritedBuildConfigurationPreventsCatalogPublication()
    {
        using var tree = new TestTree();
        tree.File("workspace/db/Commerce.sqlproj", "<Project />");
        var path = tree.File("Directory.Build.targets", "<Project />");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await new WorkspaceDiscovery().Scan([System.IO.Path.Combine(tree.Root, "workspace")], [], null, default);
        Assert.False(result.Complete);
        Assert.Contains(result.Diagnostics, d => d.Contains(path) && d.Contains("inherited build configuration"));
    }

    private static string[] Relative(TestTree tree, DiscoveryResult result) =>
        result
            .Sources.Select(x => System.IO.Path.GetRelativePath(tree.Root, x.Path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class TestTree : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "discovery-test-" + Guid.NewGuid().ToString("N"));

        public TestTree() => System.IO.Directory.CreateDirectory(Root);

        public string Directory(string relative)
        {
            var path = System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(string relative, string contents)
        {
            var path = System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Root);
            var allowed =
                System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            if (
                !resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(resolved).StartsWith("discovery-test-")
            )
                throw new InvalidOperationException("Unsafe test cleanup path.");
            System.IO.Directory.Delete(resolved, true);
        }
    }
}
