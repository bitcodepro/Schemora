using Microsoft.Data.Sqlite;
using Schemora.Host;
using Schemora.SqlServer;
using Schemora.Workspaces;
using Schemora.Workspaces.Discovery;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class JobQueueCompletionTests
{
    [Fact]
    public async Task ObservingTerminalStatusAllowsImmediateNextScanWithoutBusyRace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "job-completion-" + Guid.NewGuid().ToString("N"));
        JobQueue? queue = null;
        try
        {
            var source = Path.Combine(directory, "source");
            Directory.CreateDirectory(source);
            var store = new CatalogStore(Path.Combine(directory, "data", "catalog.sqlite"));
            var workspace = new Workspace("ws", "empty synthetic workspace", [source], [], DateTimeOffset.UtcNow);
            store.Put("Workspaces", workspace.Id, workspace.Id, workspace);
            var reader = new DacpacReader();
            var tools = new Toolchain(Path.GetDirectoryName(store.Path)!);
            var scanner = new WorkspaceScanner(
                store,
                new WorkspaceDiscovery(),
                reader,
                new StaticSqlReader(),
                new SqlSourceClassifier(),
                new SqlProjectBuilder(tools, reader)
            );
            store.Put(
                "Jobs",
                "interrupted",
                workspace.Id,
                new JobInfo("interrupted", workspace.Id, "scan:full", "running", "old", 50, DateTimeOffset.UtcNow)
            );
            queue = new JobQueue(store, scanner, new RecipeService(store, null!, null!, null!));
            await queue.StartAsync(default);
            Assert.Equal("failed", queue.Get("interrupted")!.Status);
            for (int i = 0; i < 50; i++)
            {
                var job = queue.Enqueue(workspace, "quick", i % 5 == 0 ? "missing-database" : null, false);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                JobInfo? result;
                do
                {
                    result = queue.Get(job.Id);
                    if (result?.Status is not ("queued" or "running"))
                        break;
                    await Task.Delay(1);
                } while (DateTime.UtcNow < deadline);
                Assert.NotNull(result);
                Assert.Contains(result.Status, new[] { "completed", "partial", "failed" });
                Assert.False(queue.IsActive(workspace.Id));
                Assert.False(queue.HasActiveJobs);
                // Next iteration enqueues immediately, without an arbitrary grace delay.
            }
        }
        finally
        {
            if (queue is not null)
            {
                await queue.StopAsync(default);
                queue.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
