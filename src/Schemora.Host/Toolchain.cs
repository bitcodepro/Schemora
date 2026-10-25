using System.Runtime.InteropServices;
using System.Text.Json;

namespace Schemora.Host;

public sealed record Capability(string Name, bool Available, string? Path, string? Version, string Detail);

public sealed record LegacyTools(string? MsBuild, string? SsdtTargets)
{
    public bool Ready => MsBuild is not null && SsdtTargets is not null;
}

public sealed class Toolchain(string dataDirectory)
{
    public string DataDirectory { get; } = dataDirectory;

    public static string? Find(string name, params string[] candidates)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        foreach (var extension in extensions)
        {
            try
            {
                var path = Path.Combine(directory.Trim('"'), name + extension);
                if (Path.IsPathFullyQualified(path) && File.Exists(path))
                    return path;
            }
            catch (ArgumentException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? Dotnet =>
        Find("dotnet", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));

    public static string? Docker =>
        Find(
            "docker",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "DockerDesktop",
                "resources",
                "bin",
                "docker.exe"
            ),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker",
                "Docker",
                "resources",
                "bin",
                "docker.exe"
            )
        );

    public async Task<LegacyTools> FindLegacy(CancellationToken cancellationToken = default)
    {
        var installations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe"
        );
        if (File.Exists(vswhere))
        {
            var result = await ProcessRunner.Run(
                vswhere,
                ["-all", "-products", "*", "-format", "json"],
                cancellationToken: cancellationToken
            );
            if (result.ExitCode == 0)
            {
                using var json = JsonDocument.Parse(result.Output);
                foreach (var installation in json.RootElement.EnumerateArray())
                    if (installation.TryGetProperty("installationPath", out var path) && path.GetString() is { } value)
                        installations.Add(value);
            }
        }
        foreach (var baseDirectory in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Path.Combine(Environment.GetFolderPath(baseDirectory), "Microsoft Visual Studio");
            if (!Directory.Exists(root))
                continue;
            foreach (var year in new[] { "2026", "2022", "2019", "18", "17" })
            foreach (var edition in new[] { "BuildTools", "Community", "Professional", "Enterprise" })
            {
                var path = Path.Combine(root, year, edition);
                if (Directory.Exists(path))
                    installations.Add(path);
            }
        }
        string? foundMsBuild = null,
            foundSsdt = null;
        foreach (var installation in installations.Order(StringComparer.OrdinalIgnoreCase))
        {
            var msbuild = new[] { "Current", "15.0" }
                .Select(version => Path.Combine(installation, "MSBuild", version, "Bin", "MSBuild.exe"))
                .FirstOrDefault(File.Exists);
            var targetsRoot = Path.Combine(installation, "MSBuild", "Microsoft", "VisualStudio");
            var targets = Directory.Exists(targetsRoot)
                ? Directory
                    .EnumerateDirectories(targetsRoot)
                    .OrderDescending()
                    .Select(version => Path.Combine(version, "SSDT", "Microsoft.Data.Tools.Schema.SqlTasks.targets"))
                    .FirstOrDefault(File.Exists)
                : null;
            if (msbuild is not null && targets is not null)
                return new LegacyTools(msbuild, targets);
            foundMsBuild ??= msbuild;
            foundSsdt ??= targets;
        }
        foundMsBuild ??= Find("MSBuild");
        // Never combine components from different installations into a purportedly ready toolchain.
        return new LegacyTools(foundMsBuild, foundMsBuild is null ? foundSsdt : null);
    }

    public async Task<object> Diagnose(CancellationToken cancellationToken)
    {
        var legacy = await FindLegacy(cancellationToken);
        var tasks = new[]
        {
            Probe(
                ".NET SDK",
                Dotnet,
                ["--version"],
                "Не найден .NET SDK. Установите .NET 10 SDK для SDK-style SQL-проектов.",
                cancellationToken
            ),
            Probe(
                "Docker",
                Docker,
                ["version", "--format", "{{.Server.Version}}"],
                "Docker CLI не найден; импорт DACPAC доступен без Docker.",
                cancellationToken
            ),
            Probe(
                "SqlPackage",
                Find(
                    "SqlPackage",
                    Path.Combine(DataDirectory, "tools", "sqlpackage.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "sqlpackage.exe")
                ),
                ["/Version"],
                "SqlPackage не найден; нужен только для отдельного Docker publish smoke-теста.",
                cancellationToken
            ),
        };
        var capabilities = (await Task.WhenAll(tasks)).ToList();
        capabilities.Add(
            new Capability(
                "MSBuild",
                legacy.MsBuild is not null,
                legacy.MsBuild,
                null,
                legacy.MsBuild is null
                    ? "Не найден Visual Studio MSBuild. Установите Visual Studio Build Tools с MSBuild."
                    : "MSBuild найден. Legacy SQL-проекты требуют SSDT из той же установки."
            )
        );
        capabilities.Add(
            new Capability(
                "SSDT",
                legacy.Ready,
                legacy.SsdtTargets,
                null,
                legacy.Ready
                    ? "Совместимая пара MSBuild + SSDT targets найдена."
                    : "Не найдена совместимая пара MSBuild + SQL Server Data Tools targets. Добавьте SQL Server Data Tools в Visual Studio / Build Tools или импортируйте готовый DACPAC."
            )
        );
        return new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DataDirectory,
            Capabilities = capabilities,
        };
    }

    private static async Task<Capability> Probe(
        string name,
        string? path,
        string[] arguments,
        string missing,
        CancellationToken cancellationToken
    )
    {
        if (path is null)
            return new Capability(name, false, null, null, missing);
        try
        {
            var result = await ProcessRunner.Run(path, arguments, cancellationToken: cancellationToken);
            var text = result.Output.Length > 1500 ? result.Output[..1500] : result.Output;
            return new Capability(
                name,
                result.ExitCode == 0,
                path,
                result.ExitCode == 0 ? text : null,
                result.ExitCode == 0 ? "Доступен." : $"Инструмент найден, но проверка завершилась с кодом {result.ExitCode}: {text}"
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Capability(name, false, path, null, $"Инструмент найден, но недоступен: {ex.Message}");
        }
    }
}
