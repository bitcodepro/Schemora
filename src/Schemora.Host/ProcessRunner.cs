using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Schemora.Host;

public sealed record ProcessResult(int ExitCode, string Output);

public static class ProcessRunner
{
    public static async Task<ProcessResult> Run(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        using var process = new Process();
        using var job = WindowsProcessJob.Create();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (environment is not null)
            foreach (var pair in environment)
                process.StartInfo.Environment[pair.Key] = pair.Value;
        process.Start();
        try
        {
            job?.Assign(process);
        }
        catch
        {
            TryKill(process);
            throw;
        }
        var output = Drain(process.StandardOutput, deadline.Token);
        var error = Drain(process.StandardError, deadline.Token);
        var exit = process.WaitForExitAsync(deadline.Token);
        var completion = Task.WhenAll(exit, output, error);
        try
        {
            // A child can keep inherited pipe handles open after its parent exits. The deadline must
            // cover the complete operation, not only WaitForExitAsync on the original process.
            await completion.WaitAsync(deadline.Token);
            return new ProcessResult(process.ExitCode, (await output + "\n" + await error).Trim());
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            job?.Terminate();
            TryKill(process);
            // Cancellation of pipe reads is asynchronous. Observe it without creating another
            // unbounded wait; disposing the process closes the redirected streams as a final guard.
            try
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            if (!completion.IsCompleted)
                _ = completion.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new AppException("PROCESS_TIMEOUT", "The external tool exceeded its execution time limit.", 408);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task<string> Drain(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            if (result.Length < 64 * 1024)
                result.Append(buffer, 0, Math.Min(read, 64 * 1024 - result.Length));
        return result.ToString();
    }
}
