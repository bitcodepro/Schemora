using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.Dac;
using Schemora.SqlServer;

namespace Schemora.Host;

/// <summary>Opt-in schema deployment in a disposable SQL Server container with no network or host mounts.</summary>
public sealed class DockerSchemaReader(Toolchain toolchain, DacpacReader reader, IHostApplicationLifetime? lifetime = null) : IHostedService
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";
    private const string SqlCmd = "/opt/mssql-tools18/bin/sqlcmd";
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<SchemaGraph> Read(string? input, bool allowIsolatedDocker, CancellationToken cancellationToken)
    {
        if (!allowIsolatedDocker)
            throw new AppException(
                "DOCKER_OPT_IN_REQUIRED",
                "Подтвердите запуск отдельного временного Docker-контейнера только для проверки схемы DACPAC.",
                403
            );
        var path = LocalFiles.Validate(input, ".dacpac");
        AssertSchemaOnly(path);
        var docker =
            Toolchain.Docker
            ?? throw new AppException(
                "DOCKER_MISSING",
                "Docker CLI не найден. Установите Docker Desktop или используйте импорт DACPAC без Docker.",
                422
            );
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new AppException("DOCKER_BUSY", "Другая проверка Docker уже выполняется.", 409);
        var owner = Guid.NewGuid().ToString("N");
        var name = "schemora-schema-" + owner;
        var marker = new OwnedRun(owner, name, "schemora-net-" + owner);
        var runsRoot = Path.Combine(toolchain.DataDirectory, "docker-runs");
        var stage = Path.Combine(runsRoot, owner);
        var secret = "ScH9!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var environment = new Dictionary<string, string> { ["SQLCMDPASSWORD"] = secret };
        var attempted = false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var operationStage = "prepare";
        int? failureExitCode = null;
        Exception? cleanupError = null;
        Task? generation = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime?.ApplicationStopping ?? CancellationToken.None
        );
        deadline.CancelAfter(TimeSpan.FromMinutes(6));
        var ct = deadline.Token;
        try
        {
            Directory.CreateDirectory(runsRoot);
            LocalFiles.CheckNoLinks(runsRoot);
            CreatePrivateDirectory(stage);
            await File.WriteAllTextAsync(Path.Combine(stage, "owner.json"), JsonSerializer.Serialize(marker), ct);
            var packagePath = Path.Combine(stage, "source.dacpac");
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(destination, ct);
            AssertSchemaOnly(packagePath);
            var sourceGraph = reader.Read(packagePath);
            var scriptPath = Path.Combine(stage, "schema.sql");
            operationStage = "schema-script";
            generation = Task.Run(
                () =>
                {
                    using var package = DacPackage.Load(packagePath);
                    using var output = new BoundedOutputStream(
                        new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                        64L * 1024 * 1024
                    );
                    DacServices.GenerateCreateScript(
                        output,
                        package,
                        "SchemoraSchema",
                        new DacDeployOptions
                        {
                            IgnorePreDeployScript = true,
                            IgnorePostDeployScript = true,
                            CreateNewDatabase = true,
                            IncludeTransactionalScripts = true,
                            CommandTimeout = 60,
                        }
                    );
                },
                ct
            );
            await generation.WaitAsync(ct);
            operationStage = "daemon";
            var daemon = await ProcessRunner.Run(
                docker,
                ["version", "--format", "{{.Server.Version}}"],
                timeout: TimeSpan.FromSeconds(15),
                cancellationToken: ct
            );
            if (daemon.ExitCode != 0)
                throw new AppException("DOCKER_UNAVAILABLE", "Docker daemon недоступен. Запустите Docker Desktop.", 422, daemon.Output);
            operationStage = "container-start";
            attempted = true;
            var started = await ProcessRunner.Run(
                docker,
                [
                    "run",
                    "--detach",
                    "--name",
                    name,
                    "--network",
                    "none",
                    "--label",
                    "com.schemora.owner=" + owner,
                    "--label",
                    "com.schemora.purpose=schema-preview",
                    "--memory",
                    "3g",
                    "--cpus",
                    "2",
                    "--pids-limit",
                    "1024",
                    "--env",
                    "ACCEPT_EULA=Y",
                    "--env",
                    "MSSQL_PID=Developer",
                    "--env",
                    "MSSQL_SA_PASSWORD",
                    Image,
                ],
                timeout: TimeSpan.FromMinutes(3),
                cancellationToken: ct,
                environment: new Dictionary<string, string> { ["MSSQL_SA_PASSWORD"] = secret }
            );
            if (started.ExitCode != 0 || !IsContainerId(started.Output.Trim()))
                throw new AppException(
                    "DOCKER_START_FAILED",
                    "Не удалось запустить временный SQL Server 2022. Проверьте образ и доступную память Docker.",
                    422,
                    started.Output
                );
            var containerId = started.Output.Trim();
            operationStage = "isolation-check";
            var inspected = await ProcessRunner.Run(docker, ["inspect", containerId], cancellationToken: ct);
            if (inspected.ExitCode != 0)
                throw new AppException("DOCKER_INSPECT_FAILED", "Не удалось проверить временный контейнер.", 422);
            using var json = JsonDocument.Parse(inspected.Output);
            var container = json.RootElement[0];
            if (
                container.GetProperty("Config").GetProperty("Labels").GetProperty("com.schemora.owner").GetString() != owner
                || container.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() != "none"
                || container.GetProperty("Mounts").EnumerateArray().Any(mount => mount.GetProperty("Type").GetString() == "bind")
                || container
                    .GetProperty("NetworkSettings")
                    .GetProperty("Ports")
                    .EnumerateObject()
                    .Any(property => property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0)
            )
                throw new AppException(
                    "DOCKER_ISOLATION_FAILED",
                    "Контейнер должен быть собственным, без сети, host mounts и опубликованных портов.",
                    422
                );
            operationStage = "sql-readiness";
            var readyBy = DateTimeOffset.UtcNow.AddSeconds(120);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var probe = await Exec(
                    ["-S", "tcp:127.0.0.1,1433", "-U", "sa", "-C", "-b", "-l", "3", "-Q", "SET NOCOUNT ON; SELECT 1;"],
                    TimeSpan.FromSeconds(10)
                );
                if (probe.ExitCode == 0)
                    break;
                if (DateTimeOffset.UtcNow >= readyBy)
                    throw new AppException("DOCKER_SQL_NOT_READY", "Временный SQL Server не запустился за 120 секунд.", 422, probe.Output);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            var remoteScript = "/tmp/schemora-" + owner + ".sql";
            operationStage = "script-copy";
            var copied = await ProcessRunner.Run(
                docker,
                ["cp", scriptPath, containerId + ":" + remoteScript],
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: ct
            );
            if (copied.ExitCode != 0)
                throw new AppException(
                    "DOCKER_SCRIPT_COPY_FAILED",
                    "Не удалось передать сгенерированную схему в собственный контейнер.",
                    422,
                    copied.Output
                );
            operationStage = "schema-publish";
            var publish = await Exec(
                ["-S", "tcp:127.0.0.1,1433", "-U", "sa", "-C", "-b", "-l", "5", "-t", "60", "-X1", "-i", remoteScript],
                TimeSpan.FromMinutes(2)
            );
            if (publish.ExitCode != 0)
            {
                failureExitCode = publish.ExitCode;
                throw new AppException(
                    "DOCKER_SCHEMA_PUBLISH_FAILED",
                    "Сгенерированная DacFx схема не развернулась в SQL Server 2022. Pre/post scripts и данные исключены; проверьте совместимость модели или используйте импорт DACPAC.",
                    422,
                    publish.Output
                );
            }
            operationStage = "catalog-read";
            var remoteMetadata = "/tmp/schemora-" + owner + "-catalog.txt";
            var catalog = await Exec(
                [
                    "-S",
                    "tcp:127.0.0.1,1433",
                    "-d",
                    "SchemoraSchema",
                    "-U",
                    "sa",
                    "-C",
                    "-b",
                    "-l",
                    "5",
                    "-t",
                    "60",
                    "-X1",
                    "-h",
                    "-1",
                    "-y",
                    "400",
                    "-w",
                    "65535",
                    "-u",
                    "-o",
                    remoteMetadata,
                    "-Q",
                    DockerCatalogQuery.Text,
                ],
                TimeSpan.FromMinutes(1)
            );
            if (catalog.ExitCode != 0)
            {
                var errorPath = Path.Combine(stage, "catalog-error.txt");
                var copiedError = await ProcessRunner.Run(
                    docker,
                    ["cp", containerId + ":" + remoteMetadata, errorPath],
                    cancellationToken: ct
                );
                var detail = catalog.Output;
                if (copiedError.ExitCode == 0)
                {
                    LocalFiles.CheckNoLinks(errorPath);
                    using var errorReader = new StreamReader(errorPath);
                    var buffer = new char[1500];
                    var count = await errorReader.ReadAsync(buffer.AsMemory(), ct);
                    detail = new string(buffer, 0, count);
                }
                detail = detail.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                throw new AppException(
                    "DOCKER_CATALOG_FAILED",
                    "Не удалось прочитать фактические sys metadata временного SQL Server.",
                    422,
                    detail[..Math.Min(1500, detail.Length)]
                );
            }
            var metadataPath = Path.Combine(stage, "catalog.txt");
            operationStage = "catalog-copy";
            var metadataCopy = await ProcessRunner.Run(
                docker,
                ["cp", containerId + ":" + remoteMetadata, metadataPath],
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: ct
            );
            if (metadataCopy.ExitCode == 0)
                LocalFiles.CheckNoLinks(metadataPath);
            if (metadataCopy.ExitCode != 0 || !File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 128L * 1024 * 1024)
                throw new AppException("DOCKER_CATALOG_SIZE", "Метаданные Docker недоступны или превышают допустимый размер.", 422);
            var metadataText = await File.ReadAllTextAsync(metadataPath, Encoding.Unicode, ct);
            try
            {
                return DockerCatalogQuery.Parse(metadataText, sourceGraph.DatabaseName, path);
            }
            catch (AppException error) when (error.Code == "DOCKER_CATALOG_INVALID")
            {
                var invalid = metadataText
                    .Select((value, index) => (value, index))
                    .Where(item =>
                        !char.IsAsciiLetterOrDigit(item.value) && !char.IsWhiteSpace(item.value) && item.value is not ('+' or '/' or '=')
                    )
                    .Take(16)
                    .Select(item => item.index + ":U+" + ((int)item.value).ToString("X4"));
                var padding = metadataText
                    .Select((value, index) => (value, index))
                    .Where(item => item.value == '=')
                    .Take(8)
                    .Select(item => item.index);
                throw new AppException(
                    error.Code,
                    error.Message,
                    422,
                    "Length="
                        + metadataText.Length
                        + "; nonWhitespace="
                        + metadataText.Count(value => !char.IsWhiteSpace(value))
                        + "; invalid="
                        + string.Join(',', invalid)
                        + "; paddingPositions="
                        + string.Join(',', padding)
                );
            }

            Task<ProcessResult> Exec(IEnumerable<string> arguments, TimeSpan timeout) =>
                ProcessRunner.Run(
                    docker,
                    new[] { "exec", "--env", "SQLCMDPASSWORD", containerId, SqlCmd }.Concat(arguments),
                    timeout: timeout,
                    cancellationToken: ct,
                    environment: environment
                );
        }
        catch (AppException error)
        {
            throw new AppException(
                error.Code,
                error.Message,
                error.Status,
                DockerFailureDiagnostics.Record(
                    toolchain.DataDirectory,
                    operationStage,
                    timer.ElapsedMilliseconds,
                    error,
                    secret,
                    failureExitCode
                )
            );
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && lifetime?.ApplicationStopping.IsCancellationRequested != true)
        {
            var error = new AppException("DOCKER_TIMEOUT", "Isolated Docker validation exceeded six minutes.", 408);
            throw new AppException(
                error.Code,
                error.Message,
                error.Status,
                DockerFailureDiagnostics.Record(toolchain.DataDirectory, operationStage, timer.ElapsedMilliseconds, error, secret)
            );
        }
        catch (UnauthorizedAccessException)
        {
            throw new AppException(
                "DOCKER_PRIVATE_STAGE_DENIED",
                "Нельзя создать или защитить временный каталог Docker. Нужен каталог данных с правом создания приватного ACL; защита не ослаблялась.",
                422
            );
        }
        catch (DacServicesException cause)
        {
            var error = new AppException(
                "DOCKER_SCHEMA_SCRIPT_FAILED",
                "DacFx could not generate the schema-only deployment script.",
                422,
                cause.Message
            );
            throw new AppException(
                error.Code,
                error.Message,
                error.Status,
                DockerFailureDiagnostics.Record(toolchain.DataDirectory, operationStage, timer.ElapsedMilliseconds, error, secret)
            );
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (attempted)
            {
                try
                {
                    await CleanupOwned(docker, marker, cleanup.Token);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    cleanupError = error;
                }
            }
            if (generation is { IsCompleted: false })
            {
                try
                {
                    await generation.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                if (!generation.IsCompleted)
                    _ = generation.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    );
            }
            try
            {
                if (cleanupError is null && Directory.Exists(stage))
                {
                    var resolved = Path.GetFullPath(stage);
                    if (Path.GetDirectoryName(resolved) != Path.GetFullPath(runsRoot) || Path.GetFileName(resolved) != owner)
                        throw new InvalidOperationException("Unsafe stage cleanup target.");
                    LocalFiles.CheckNoLinks(resolved);
                    Directory.Delete(resolved, true);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                cleanupError ??= error;
            }
            gate.Release();
            if (cleanupError is not null)
                throw new AppException(
                    "DOCKER_CLEANUP_FAILED",
                    "Не удалось полностью удалить временные ресурсы. Проверьте собственный контейнер "
                        + name
                        + " и каталог docker-runs/"
                        + owner
                        + " в каталоге данных.",
                    500
                );
        }
    }

    public IReadOnlyList<SchemaDiagnostic> RecoveryDiagnostics { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var runsRoot = Path.Combine(toolchain.DataDirectory, "docker-runs");
        if (!Directory.Exists(runsRoot))
            return;
        var diagnostics = new List<SchemaDiagnostic>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            LocalFiles.CheckNoLinks(runsRoot);
            var directories = Directory.EnumerateDirectories(runsRoot).Order(StringComparer.Ordinal).Take(21).ToArray();
            if (directories.Length > 20)
                diagnostics.Add(
                    new(
                        "warning",
                        "DOCKER_RECOVERY_LIMIT",
                        "Проверены первые 20 временных Docker-запусков; оставшиеся будут проверены при следующем запуске."
                    )
                );
            foreach (var directory in directories.Take(20))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                    continue;
                LocalFiles.CheckNoLinks(directory);
                var markerPath = Path.Combine(directory, "owner.json");
                if (!File.Exists(markerPath))
                    continue;
                LocalFiles.CheckNoLinks(markerPath);
                if (new FileInfo(markerPath).Length > 4096)
                    throw new InvalidDataException("Marker too large.");
                var marker = JsonSerializer.Deserialize<OwnedRun>(await File.ReadAllTextAsync(markerPath, deadline.Token));
                var owner = Path.GetFileName(directory);
                if (
                    marker is null
                    || marker.Owner != owner
                    || marker.ContainerName != "schemora-schema-" + owner
                    || marker.NetworkName != "schemora-net-" + owner
                )
                    throw new InvalidDataException("Marker identity mismatch.");
                var docker = Toolchain.Docker ?? throw new InvalidOperationException("Docker unavailable for recovery.");
                await CleanupOwned(docker, marker, deadline.Token);
                // Exact GUID child verified above; marker and ownership labels are both required before cleanup.
                Directory.Delete(directory, true);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            diagnostics.Add(
                new(
                    "warning",
                    "DOCKER_RECOVERY_PENDING",
                    "Очистка собственного предыдущего Docker-запуска отложена: Docker недоступен или проверка владельца не прошла. Каталог docker-runs сохранён; чужие ресурсы не удалялись."
                )
            );
        }
        RecoveryDiagnostics = diagnostics;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // ApplicationStopping cancels the active Read immediately; await its finally before normal host shutdown.
        await gate.WaitAsync(cancellationToken);
        gate.Release();
    }

    private static async Task CleanupOwned(string docker, OwnedRun marker, CancellationToken ct)
    {
        var containers = await ProcessRunner.Run(
            docker,
            ["ps", "--all", "--no-trunc", "--filter", "label=com.schemora.owner=" + marker.Owner, "--format", "{{.ID}}|{{.Names}}"],
            cancellationToken: ct
        );
        if (containers.ExitCode != 0)
            throw new InvalidOperationException("Container inventory failed.");
        foreach (var row in containers.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = row.Split('|');
            if (parts.Length != 2 || !IsContainerId(parts[0]) || parts[1] != marker.ContainerName)
                throw new InvalidOperationException("Container ownership mismatch.");
            var owner = await ProcessRunner.Run(
                docker,
                ["inspect", "--format", "{{ index .Config.Labels \"com.schemora.owner\" }}", parts[0]],
                cancellationToken: ct
            );
            if (owner.ExitCode != 0 || owner.Output.Trim() != marker.Owner)
                throw new InvalidOperationException("Container ownership mismatch.");
            var removed = await ProcessRunner.Run(
                docker,
                ["rm", "--force", "--volumes", parts[0]],
                timeout: TimeSpan.FromSeconds(15),
                cancellationToken: ct
            );
            if (removed.ExitCode != 0)
                throw new InvalidOperationException("Container removal failed.");
        }
        var remaining = await ProcessRunner.Run(
            docker,
            ["ps", "--all", "--quiet", "--filter", "label=com.schemora.owner=" + marker.Owner],
            cancellationToken: ct
        );
        if (remaining.ExitCode != 0 || remaining.Output.Length != 0)
            throw new InvalidOperationException("Container absence could not be verified.");
        var networks = await ProcessRunner.Run(
            docker,
            ["network", "ls", "--no-trunc", "--filter", "label=com.schemora.owner=" + marker.Owner, "--format", "{{.ID}}|{{.Name}}"],
            cancellationToken: ct
        );
        if (networks.ExitCode != 0)
            throw new InvalidOperationException("Network inventory failed.");
        foreach (var row in networks.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = row.Split('|');
            if (parts.Length != 2 || !IsContainerId(parts[0]) || parts[1] != marker.NetworkName)
                throw new InvalidOperationException("Network ownership mismatch.");
            var owner = await ProcessRunner.Run(
                docker,
                ["network", "inspect", "--format", "{{ index .Labels \"com.schemora.owner\" }}", parts[0]],
                cancellationToken: ct
            );
            if (owner.ExitCode != 0 || owner.Output.Trim() != marker.Owner)
                throw new InvalidOperationException("Network ownership mismatch.");
            var removed = await ProcessRunner.Run(docker, ["network", "rm", parts[0]], cancellationToken: ct);
            if (removed.ExitCode != 0)
                throw new InvalidOperationException("Network removal failed.");
        }
        var remainingNetworks = await ProcessRunner.Run(
            docker,
            ["network", "ls", "--quiet", "--filter", "label=com.schemora.owner=" + marker.Owner],
            cancellationToken: ct
        );
        if (remainingNetworks.ExitCode != 0 || remainingNetworks.Output.Length != 0)
            throw new InvalidOperationException("Network absence could not be verified.");
    }

    private sealed record OwnedRun(string Owner, string ContainerName, string NetworkName);

    public static void AssertSchemaOnly(string path)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model.xml",
            "DacMetadata.xml",
            "Origin.xml",
            "[Content_Types].xml",
        };
        try
        {
            using var archive = ZipFile.OpenRead(path);
            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!allowed.Contains(entry.FullName) || !names.Add(entry.FullName))
                    throw new AppException(
                        "DACPAC_SCHEMA_ONLY_REQUIRED",
                        "Docker принимает только schema-only DACPAC без pre/post scripts, данных и дополнительных payload entries.",
                        422
                    );
                total += entry.Length;
                if (entry.Length > 128L * 1024 * 1024 || total > 256L * 1024 * 1024)
                    throw new AppException("DACPAC_EXPANDED_TOO_LARGE", "Распакованный DACPAC превышает допустимый объём метаданных.", 422);
            }
            if (!names.Contains("model.xml") || !names.Contains("DacMetadata.xml"))
                throw new AppException("DACPAC_INVALID", "DACPAC не содержит обязательных метаданных схемы.", 422);
        }
        catch (InvalidDataException)
        {
            throw new AppException("DACPAC_INVALID", "Файл не является корректным DACPAC ZIP-пакетом.", 422);
        }
    }

    private static bool IsContainerId(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var sid = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(sid);
            security.AddAccessRule(
                new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
            new DirectoryInfo(path).Create(security);
        }
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
