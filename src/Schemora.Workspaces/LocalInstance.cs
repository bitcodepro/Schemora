using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Schemora.Workspaces;

/// <summary>One local host per data directory. Ownership ends when the handle closes, including a crash.</summary>
public sealed class LocalInstance : IDisposable
{
    private readonly FileStream handle;
    private readonly string infoPath;
    private readonly bool privateRuntime;

    public sealed record RunningInstance(string Origin, string Token, int ProcessId);

    private LocalInstance(FileStream handle, string directory, bool privateRuntime)
    {
        this.handle = handle;
        this.privateRuntime = privateRuntime;
        infoPath = System.IO.Path.Combine(directory, "running.json");
    }

    public static LocalInstance? TryAcquire(string directory)
    {
        Directory.CreateDirectory(directory);
        var runtime = System.IO.Path.Combine(directory, "runtime");
        Directory.CreateDirectory(runtime);
        var privateRuntime = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                var owner = WindowsIdentity.GetCurrent().User!;
                security.AddAccessRule(
                    new FileSystemAccessRule(
                        owner,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow
                    )
                );
                new DirectoryInfo(runtime).SetAccessControl(security);
            }
            else
                File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (UnauthorizedAccessException)
        {
            privateRuntime = false;
        }
        catch (PlatformNotSupportedException)
        {
            privateRuntime = false;
        }
        // If a custom data directory cannot enforce private ACLs, keep the instance lock
        // but do not persist the launch token. The explicit --session-file option is unchanged.
        try
        {
            return new(
                new FileStream(
                    System.IO.Path.Combine(runtime, "instance.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                ),
                runtime,
                privateRuntime
            );
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static RunningInstance? ReadRunning(string directory)
    {
        try
        {
            var info = JsonSerializer.Deserialize<RunningInstance>(
                File.ReadAllText(System.IO.Path.Combine(directory, "runtime", "running.json")),
                CatalogStore.Json
            );
            if (
                info is null
                || !Uri.TryCreate(info.Origin, UriKind.Absolute, out var uri)
                || uri.Scheme != "http"
                || uri.Host != "127.0.0.1"
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || info.Token.Length != 64
                || info.Token.Any(c => !Uri.IsHexDigit(c))
            )
                return null;
            using var process = System.Diagnostics.Process.GetProcessById(info.ProcessId);
            return process.HasExited ? null : info;
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Publish(string origin, string token)
    {
        if (privateRuntime)
            File.WriteAllText(
                infoPath,
                JsonSerializer.Serialize(new RunningInstance(origin, token, Environment.ProcessId), CatalogStore.Json)
            );
    }

    public void Dispose()
    {
        try
        {
            File.Delete(infoPath);
        }
        finally
        {
            handle.Dispose();
        }
    }
}
