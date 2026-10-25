using System.Diagnostics;

// Test-only fault injection. All Docker commands except schema publication use the real CLI.
if (args.Length > 0 && args[0] == "exec" && args.Contains("-i"))
{
    Console.Error.WriteLine("Sqlcmd: synthetic deployment error 102; Password=" + Environment.GetEnvironmentVariable("SQLCMDPASSWORD"));
    return 1;
}
var executable =
    Environment.GetEnvironmentVariable("SCHEMORA_TEST_REAL_DOCKER")
    ?? throw new InvalidOperationException("Test Docker executable is required.");
using var process = new Process
{
    StartInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    },
};
foreach (var arg in args)
    process.StartInfo.ArgumentList.Add(arg);
process.Start();
await Task.WhenAll(
    process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()),
    process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()),
    process.WaitForExitAsync()
);
return process.ExitCode;
