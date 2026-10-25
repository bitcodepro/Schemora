#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Run,
    [string]$DockerPath,
    [ValidateRange(30, 900)][int]$StartupTimeoutSeconds = 180
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Run) {
    throw 'Opt-in required: pwsh -File scripts/Test-Docker.ps1 -Run. This builds the synthetic Commerce fixture, starts an owned temporary SQL Server container and removes it in finally.'
}
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo 'work'
$tools = Join-Path $work 'tools'
$project = Join-Path $repo 'tests/Fixtures/Commerce/Commerce.sqlproj'
$packagePath = Join-Path $repo 'tests/Fixtures/Commerce/bin/Debug/Commerce.dacpac'
$image = 'mcr.microsoft.com/mssql/server:2022-latest'
$owner = [Guid]::NewGuid().ToString('N')
$containerName = "schemora-smoke-$owner"
$containerId = $null
$creationAttempted = $false
$secret = 'ScH9!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$privateDirectory = Join-Path $work "docker-private-$owner"
$failure = $null
$report = [ordered]@{
    status = 'Running'; startedAtUtc = [DateTime]::UtcNow.ToString('O')
    fixture = 'tests/Fixtures/Commerce/Commerce.sqlproj'; image = $image
    container = $containerName; cleanedUp = $false; expectedTables = 4
    expectedColumns = 13; expectedForeignKeys = 1; expectedForeignKeyColumns = 2
}

function Invoke-Tool {
    param([string]$File, [string[]]$Arguments, [hashtable]$Environment = @{}, [int]$TimeoutSeconds = 120, [switch]$AllowFailure)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in $Arguments) { [void]$start.ArgumentList.Add($arg) }
    foreach ($item in $Environment.GetEnumerator()) { $start.Environment[$item.Key] = [string]$item.Value }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            $process.WaitForExit()
            throw "Tool timed out after $TimeoutSeconds seconds: $([IO.Path]::GetFileName($File))"
        }
        $result = [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult().Replace($secret, '[REDACTED]'); Error = $stderr.GetAwaiter().GetResult().Replace($secret, '[REDACTED]') }
        if ($result.ExitCode -ne 0 -and -not $AllowFailure) {
            throw "$([IO.Path]::GetFileName($File)) failed ($($result.ExitCode)): $($result.Error) $($result.Output)"
        }
        return $result
    } finally { $process.Dispose() }
}

function Assert-SchemaOnlyDacpac {
    param([string]$Path)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ('model.xml' -notin $entries) { throw 'DACPAC is missing model.xml.' }
        # Refuse script and data payloads even though publish also explicitly disables scripts.
        $unsafe = @($entries | Where-Object { $_ -match '(?i)(pre[-_]?deploy|post[-_]?deploy|(^|/)data(/|\.)|\.(bcp|csv|dat)$)' })
        if ($unsafe.Count -gt 0) { throw "Schema-only smoke refuses DACPAC script/data entries: $($unsafe -join ', ')" }
    } finally { $zip.Dispose() }
}

try {
    New-Item -ItemType Directory -Force -Path $work, $tools | Out-Null
    if (-not $DockerPath) {
        $found = Get-Command docker -ErrorAction SilentlyContinue
        if ($found) { $DockerPath = $found.Source }
        else {
            $candidate = Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/resources/bin/docker.exe'
            if (Test-Path -LiteralPath $candidate) { $DockerPath = $candidate }
        }
    }
    if (-not $DockerPath -or -not (Test-Path -LiteralPath $DockerPath -PathType Leaf)) { throw 'Docker CLI unavailable; pass -DockerPath with the Docker executable path.' }
    Write-Host 'Checking Docker daemon and building the synthetic Commerce fixture...'
    $report.dockerVersion = (Invoke-Tool $DockerPath @('version', '--format', '{{.Server.Version}}')).Output.Trim()
    [void](Invoke-Tool 'dotnet' @('build', $project, '--configuration', 'Debug', '--nologo') -TimeoutSeconds 300)
    if (-not (Test-Path -LiteralPath $packagePath)) { throw "Fixture build did not produce $packagePath" }
    Assert-SchemaOnlyDacpac $packagePath
    $report.dacpacSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash

    $sqlpackage = Join-Path $tools $(if ($IsWindows) { 'sqlpackage.exe' } else { 'sqlpackage' })
    if (-not (Test-Path -LiteralPath $sqlpackage)) {
        Write-Host 'Installing pinned Microsoft.SqlPackage 170.5.96 under work/tools...'
        [void](Invoke-Tool 'dotnet' @('tool', 'install', 'Microsoft.SqlPackage', '--tool-path', $tools, '--version', '170.5.96', '--add-source', 'https://api.nuget.org/v3/index.json') -TimeoutSeconds 300)
    }
    $report.sqlPackageVersion = (Invoke-Tool $sqlpackage @('/Version')).Output.Trim()

    # Restrict the temporary credential directory before writing any credential material.
    [void](New-Item -ItemType Directory -Path $privateDirectory)
    if ($IsWindows) {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.SetOwner($sid)
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $privateDirectory -AclObject $acl
    } else { [IO.File]::SetUnixFileMode($privateDirectory, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }

    Write-Host "Starting isolated $containerName (only synthetic schema, no host mounts)..."
    $creationAttempted = $true
    $created = Invoke-Tool $DockerPath @('run', '--detach', '--name', $containerName, '--label', "com.schemora.owner=$owner", '--label', 'com.schemora.purpose=phase0-smoke', '--publish', '127.0.0.1::1433', '--memory', '3g', '--cpus', '2', '--env', 'ACCEPT_EULA=Y', '--env', 'MSSQL_PID=Developer', '--env', 'MSSQL_SA_PASSWORD', $image) -Environment @{ MSSQL_SA_PASSWORD = $secret } -TimeoutSeconds 600
    $containerId = $created.Output.Trim()
    if ($containerId -notmatch '^[0-9a-f]{64}$') { throw 'Docker returned an unexpected container ID.' }
    $inspection = (Invoke-Tool $DockerPath @('inspect', $containerId)).Output | ConvertFrom-Json
    $portBindings = @($inspection[0].NetworkSettings.Ports.'1433/tcp')
    if ($portBindings.Count -ne 1 -or $portBindings[0].HostIp -ne '127.0.0.1') { throw 'Container SQL port is not bound exclusively to IPv4 loopback.' }
    $port = [int]$portBindings[0].HostPort
    $report.imageId = $inspection[0].Image
    $report.loopbackOnly = $true
    $report.hostPort = $port

    $sqlcmdPath = '/opt/mssql-tools18/bin/sqlcmd'
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    $lastProgress = [DateTime]::MinValue
    do {
        $check = Invoke-Tool $DockerPath @('exec', '--env', 'SQLCMDPASSWORD', $containerId, $sqlcmdPath, '-S', 'localhost', '-U', 'sa', '-C', '-b', '-l', '3', '-Q', 'SET NOCOUNT ON; SELECT 1;') -Environment @{ SQLCMDPASSWORD = $secret } -TimeoutSeconds 15 -AllowFailure
        if ($check.ExitCode -eq 0) { $ready = $true; break }
        if (($check.Error + $check.Output) -match 'no such file or directory|executable file not found') { throw "SQL Server image is missing expected sqlcmd: $sqlcmdPath" }
        if (([DateTime]::UtcNow - $lastProgress).TotalSeconds -ge 15) { Write-Host 'Waiting for local SQL Server readiness...'; $lastProgress = [DateTime]::UtcNow }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw "SQL Server was not ready after $StartupTimeoutSeconds seconds." }

    $profilePath = Join-Path $privateDirectory 'schema.publish.xml'
    $connection = "Server=tcp:127.0.0.1,$port;Initial Catalog=SchemoraSmoke;User ID=sa;Password=$secret;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15"
    $escapedConnection = [Security.SecurityElement]::Escape($connection)
    $profile = "<Project ToolsVersion=`"4.0`" xmlns=`"http://schemas.microsoft.com/developer/msbuild/2003`"><PropertyGroup><TargetConnectionString>$escapedConnection</TargetConnectionString><ProfileVersionNumber>1</ProfileVersionNumber></PropertyGroup></Project>"
    [IO.File]::WriteAllText($profilePath, $profile, [Text.UTF8Encoding]::new($false))
    Write-Host 'Publishing schema with pre/post-deployment scripts disabled...'
    [void](Invoke-Tool $sqlpackage @('/Action:Publish', "/SourceFile:$packagePath", "/Profile:$profilePath", '/p:IgnorePreDeployScript=True', '/p:IgnorePostDeployScript=True', '/p:IncludeTransactionalScripts=True', '/p:CommandTimeout=60') -TimeoutSeconds 180)
    Remove-Item -LiteralPath $profilePath -Force
    $connection = $null
    $profile = $null

    $query = @"
SET NOCOUNT ON;
SELECT
    CONVERT(nvarchar(50), SERVERPROPERTY('ProductVersion')) AS sqlServerVersion,
    (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0) AS tableCount,
    (SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.is_ms_shipped = 0) AS columnCount,
    (SELECT COUNT(*) FROM sys.foreign_keys WHERE is_ms_shipped = 0) AS foreignKeyCount,
    (SELECT COUNT(*) FROM sys.foreign_key_columns) AS foreignKeyColumnCount,
    (SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'PK') AS primaryKeyCount,
    JSON_QUERY((SELECT s.name AS [schema], t.name AS [name] FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0 ORDER BY s.name, t.name FOR JSON PATH)) AS tables,
    JSON_QUERY((SELECT fkc.constraint_column_id AS ordinal, c.name AS sourceColumn, rc.name AS targetColumn FROM sys.foreign_key_columns fkc JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id ORDER BY fkc.constraint_column_id FOR JSON PATH)) AS foreignKeyColumns
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
"@
    Write-Host 'Reading and verifying sys catalog metadata...'
    $catalogResult = Invoke-Tool $DockerPath @('exec', '--env', 'SQLCMDPASSWORD', $containerId, $sqlcmdPath, '-S', 'localhost', '-d', 'SchemoraSmoke', '-U', 'sa', '-C', '-b', '-l', '5', '-h', '-1', '-w', '65535', '-y', '4000', '-Q', $query) -Environment @{ SQLCMDPASSWORD = $secret }
    $catalog = $catalogResult.Output.Trim() | ConvertFrom-Json
    if ($catalog.tableCount -ne 4 -or $catalog.columnCount -ne 13 -or $catalog.foreignKeyCount -ne 1 -or $catalog.foreignKeyColumnCount -ne 2 -or $catalog.primaryKeyCount -ne 4) { throw "Catalog did not match fixture counts: $($catalog | ConvertTo-Json -Compress -Depth 5)" }
    $names = @($catalog.tables | ForEach-Object { "$($_.schema).$($_.name)" })
    if (($names -join '|') -ne 'dbo.Customers|dbo.Documents|sales.Customers|sales.Orders') { throw 'Schema-qualified table names did not match the fixture.' }
    if (($catalog.foreignKeyColumns.sourceColumn -join '|') -ne 'TenantId|CustomerId' -or ($catalog.foreignKeyColumns.targetColumn -join '|') -ne 'TenantId|CustomerId') { throw 'Composite FK column ordering did not match the fixture.' }
    $report.catalog = $catalog
    $report.status = 'Passed'
} catch {
    $failure = $_.Exception.Message.Replace($secret, '[REDACTED]')
    $report.status = 'Failed'
    $report.error = $failure
} finally {
    # Docker may have created the container even if its client timed out before returning the ID.
    if ($creationAttempted -and (-not $containerId -or $containerId -notmatch '^[0-9a-f]{64}$')) {
        try {
            $lookup = Invoke-Tool $DockerPath @('inspect', '--format', '{{.Id}}|{{ index .Config.Labels "com.schemora.owner" }}', $containerName) -AllowFailure
            if ($lookup.ExitCode -eq 0) {
                $identity = $lookup.Output.Trim().Split('|')
                if ($identity.Count -ne 2 -or $identity[1] -ne $owner -or $identity[0] -notmatch '^[0-9a-f]{64}$') { throw 'Refusing fallback cleanup: ownership does not match this invocation.' }
                $containerId = $identity[0]
            }
        } catch {
            $report.cleanupError = $_.Exception.Message.Replace($secret, '[REDACTED]')
            $report.status = 'Failed'
            if (-not $failure) { $failure = $report.cleanupError }
        }
    }
    if ($containerId -and $containerId -match '^[0-9a-f]{64}$') {
        try {
            $owned = (Invoke-Tool $DockerPath @('inspect', '--format', '{{ index .Config.Labels "com.schemora.owner" }}', $containerId)).Output.Trim()
            if ($owned -ne $owner) { throw 'Refusing container cleanup: ownership label does not match this invocation.' }
            Write-Host 'Removing only this invocation''s owned SQL Server container...'
            [void](Invoke-Tool $DockerPath @('rm', '--force', '--volumes', $containerId))
            $report.cleanedUp = $true
        } catch {
            $report.cleanupError = $_.Exception.Message.Replace($secret, '[REDACTED]')
            $report.status = 'Failed'
            if (-not $failure) { $failure = $report.cleanupError }
        }
    }
    if (Test-Path -LiteralPath $privateDirectory) {
        # Exact per-invocation directory, resolved and checked before recursive removal.
        $resolvedPrivate = [IO.Path]::GetFullPath($privateDirectory)
        $resolvedWork = [IO.Path]::GetFullPath($work).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedPrivate.StartsWith($resolvedWork, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedPrivate) -ne "docker-private-$owner") { throw 'Unsafe private directory cleanup target.' }
        Remove-Item -LiteralPath $resolvedPrivate -Recurse -Force
    }
    $report.completedAtUtc = [DateTime]::UtcNow.ToString('O')
    $reportPath = Join-Path $work 'docker-report.json'
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $secret = $null
}
if ($failure) { throw $failure }
Write-Host "PASS: 4 tables, 13 columns, 4 PKs, 1 composite FK (2 ordered columns). Container removed. Report: $reportPath"
