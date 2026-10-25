using System.Diagnostics;
using Schemora.Host;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "ProcessArtifacts", Guid.NewGuid().ToString("N"));
    private static string Node =>
        Toolchain.Find("node") ?? throw new InvalidOperationException("Node.js is required for the process lifecycle regression tests.");

    public ProcessRunnerTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task CapturesStandardOutputAndErrorAndExitCode()
    {
        var result = await ProcessRunner.Run(
            Node,
            ["-e", "process.stdout.write('stdout-marker'); process.stderr.write('stderr-marker'); process.exitCode = 7;"],
            timeout: TimeSpan.FromSeconds(10)
        );
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("stdout-marker", result.Output);
        Assert.Contains("stderr-marker", result.Output);
    }

    [Fact]
    public async Task TimeoutStopsLongRunningTool()
    {
        var pidFile = Path.Combine(directory, "timeout.pid");
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<AppException>(() =>
            ProcessRunner.Run(
                Node,
                ["-e", "require('node:fs').writeFileSync(process.argv[1], String(process.pid)); setInterval(() => {}, 1000);", pidFile],
                timeout: TimeSpan.FromSeconds(2)
            )
        );
        Assert.Equal("PROCESS_TIMEOUT", error.Code);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Timeout took {elapsed.Elapsed}.");
        Assert.True(File.Exists(pidFile), "The test tool must start before its timeout.");
        await AssertStopped(int.Parse(await File.ReadAllTextAsync(pidFile)));
    }

    [Fact]
    public async Task CancellationStopsParentAndItsChild()
    {
        var pidFile = Path.Combine(directory, "cancel.json");
        using var cancellation = new CancellationTokenSource();
        var run = ProcessRunner.Run(
            Node,
            [
                "-e",
                """
                const child = require('node:child_process').spawn(process.execPath,
                    ['-e', 'setInterval(() => {}, 1000)'], { stdio: ['ignore', 'inherit', 'inherit'] });
                require('node:fs').writeFileSync(process.argv[1], JSON.stringify([process.pid, child.pid]));
                setInterval(() => {}, 1000);
                """,
                pidFile,
            ],
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: cancellation.Token
        );
        await WaitForFile(pidFile);
        var pids = System.Text.Json.JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(pidFile))!;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        foreach (var pid in pids)
            await AssertStopped(pid);
    }

    [Fact]
    public async Task TimeoutStillAppliesWhenExitedParentLeavesChildHoldingOutputPipes()
    {
        if (!OperatingSystem.IsWindows())
            return; // Windows is the supported Phase 0 process-isolation platform.
        var pidFile = Path.Combine(directory, "orphan.pid");
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<AppException>(() =>
            ProcessRunner.Run(
                Node,
                [
                    "-e",
                    """
                    const child = require('node:child_process').spawn(process.execPath,
                        ['-e', 'process.stdout.write("child-started"); setInterval(() => {}, 1000)'],
                        { detached: true, stdio: ['ignore', 'inherit', 'inherit'] });
                    require('node:fs').writeFileSync(process.argv[1], String(child.pid));
                    process.exit(0);
                    """,
                    pidFile,
                ],
                timeout: TimeSpan.FromSeconds(2)
            )
        );
        Assert.Equal("PROCESS_TIMEOUT", error.Code);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Inherited output pipes delayed timeout to {elapsed.Elapsed}.");
        Assert.True(File.Exists(pidFile), "The parent must spawn the pipe-holding child.");
        await AssertStopped(int.Parse(await File.ReadAllTextAsync(pidFile)));
    }

    private static async Task WaitForFile(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Assert.True(File.Exists(path), "The test process did not finish starting within five seconds.");
    }

    private static async Task AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(process.HasExited, $"Process {pid} is still running.");
        }
        catch (ArgumentException) { } // Already reaped by Windows.
    }

    public void Dispose()
    {
        // Every artifact path is generated under this test's own output directory.
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
