[CmdletBinding()]
param([string]$Executable, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDefaultExecutable = -not $Executable
if ($taskDefaultExecutable) { $Executable = Join-Path $taskRoot 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe' }
$taskRunId = [guid]::NewGuid().ToString('N')
$taskRun = Join-Path $taskRoot ('work/workspace-smoke-' + $taskRunId)
$taskFixtureRoot = Join-Path $taskRoot ('work/fixtures-' + $taskRunId)
$taskDataDirectory = Join-Path $taskRun 'data'
$taskReportPath = Join-Path $taskRoot 'work/workspace-report.json'
New-Item -ItemType Directory -Path $taskRun, $taskFixtureRoot -Force | Out-Null
$taskChecks = [Collections.Generic.List[object]]::new()
$script:taskHost = $null
$script:taskSessionPath = $null
$script:taskBase = $null
$script:taskHeaders = $null
$script:taskHostOutput = $null
$script:taskHostError = $null
$taskStartedAt = [datetime]::UtcNow
$taskSuccess = $false
$taskFailure = $null
$taskExecutableHash = $null

function Assert-Task([bool]$Condition, [string]$Name, [object]$Details = $null) {
    $taskChecks.Add([ordered]@{ name = $Name; passed = $Condition; details = $Details })
    if (-not $Condition) { throw ('Acceptance failed: ' + $Name) }
}
function Copy-TaskTree([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($taskEntry in Get-ChildItem -LiteralPath $Source -Force) {
        if (($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Fixture contains a link: ' + $taskEntry.FullName) }
        if ($taskEntry.PSIsContainer) {
            if ($taskEntry.Name -notin @('bin', 'obj', '.git', 'node_modules')) { Copy-TaskTree $taskEntry.FullName (Join-Path $Destination $taskEntry.Name) }
        } else { Copy-Item -LiteralPath $taskEntry.FullName -Destination (Join-Path $Destination $taskEntry.Name) }
    }
}
function Get-TaskTreeState([string]$Root, [switch]$SourceFilesOnly) {
    $taskEntries = [Collections.Generic.List[string]]::new()
    $taskPending = [Collections.Generic.Stack[string]]::new()
    $taskPending.Push((Resolve-Path -LiteralPath $Root).Path)
    while ($taskPending.Count -gt 0) {
        foreach ($taskEntry in Get-ChildItem -LiteralPath $taskPending.Pop() -Force) {
            if (($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Fixture contains a link: ' + $taskEntry.FullName) }
            if ($taskEntry.PSIsContainer) {
                if (-not $SourceFilesOnly -or $taskEntry.Name -notin @('bin', 'obj', '.git', 'node_modules')) {
                    $taskEntries.Add('directory:' + [IO.Path]::GetRelativePath($Root, $taskEntry.FullName))
                    $taskPending.Push($taskEntry.FullName)
                }
            } else { $taskEntries.Add([IO.Path]::GetRelativePath($Root, $taskEntry.FullName) + ':' + (Get-FileHash -LiteralPath $taskEntry.FullName -Algorithm SHA256).Hash) }
        }
    }
    return (($taskEntries | Sort-Object) -join "`n")
}
function Invoke-TaskApi([string]$Method, [string]$Path, [object]$Body = $null) {
    $taskRequest = @{ Uri = $script:taskBase + $Path; Method = $Method; Headers = $script:taskHeaders; TimeoutSec = 45 }
    if ($null -ne $Body) { $taskRequest.ContentType = 'application/json'; $taskRequest.Body = $Body | ConvertTo-Json -Depth 15 -Compress }
    $taskResponse = Invoke-RestMethod @taskRequest
    return $taskResponse
}
function Start-TaskHost {
    $script:taskSessionPath = Join-Path $taskRun ('session-' + [guid]::NewGuid().ToString('N') + '.json')
    $script:taskHost = [Diagnostics.Process]::new()
    $script:taskHost.StartInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $script:taskHost.StartInfo.UseShellExecute = $false
    $script:taskHost.StartInfo.CreateNoWindow = $true
    $script:taskHost.StartInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $script:taskHost.StartInfo.RedirectStandardOutput = $true
    $script:taskHost.StartInfo.RedirectStandardError = $true
    $script:taskHost.StartInfo.WorkingDirectory = $taskRun
    foreach ($taskArgument in @('--no-browser', '--session-file', $script:taskSessionPath, '--data-dir', $taskDataDirectory)) { $script:taskHost.StartInfo.ArgumentList.Add($taskArgument) }
    $script:taskHost.Start() | Out-Null
    $script:taskHostOutput = $script:taskHost.StandardOutput.ReadToEndAsync()
    $script:taskHostError = $script:taskHost.StandardError.ReadToEndAsync()
    $taskDeadline = [datetime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $script:taskSessionPath)) {
        if ($script:taskHost.HasExited) { throw ('Host exited during startup: ' + $script:taskHostOutput.Result + $script:taskHostError.Result) }
        if ([datetime]::UtcNow -gt $taskDeadline) { throw 'Host startup timed out.' }
        Start-Sleep -Milliseconds 100
    }
    # The token is read privately and is never included in stdout or the report.
    $taskSession = Get-Content -LiteralPath $script:taskSessionPath -Raw | ConvertFrom-Json
    if ($taskSession.processId -ne $script:taskHost.Id) { throw 'Session file belongs to an unexpected process.' }
    $script:taskBase = $taskSession.origin
    $script:taskHeaders = @{ 'X-Schemora-Token' = $taskSession.token; Origin = $taskSession.origin }
    Invoke-TaskApi Get '/api/health' | Out-Null
}
function Stop-TaskHost {
    if ($null -ne $script:taskHost) {
        if (-not $script:taskHost.HasExited) {
            # Only this exact child process is stopped; no name-based cleanup.
            $script:taskHost.Kill($true)
            $script:taskHost.WaitForExit()
        }
        $script:taskHost.Dispose()
        $script:taskHost = $null
    }
    if ($script:taskSessionPath -and (Test-Path -LiteralPath $script:taskSessionPath)) { Remove-Item -LiteralPath $script:taskSessionPath -Force }
    $script:taskHeaders = $null
}
function Start-TaskScan([string]$WorkspaceId, [string]$Mode) {
    return Invoke-TaskApi Post ('/api/workspaces/' + $WorkspaceId + '/scan') @{ mode = $Mode; trustProjects = $false }
}
function Wait-TaskJob([string]$Id) {
    $taskDeadline = [datetime]::UtcNow.AddSeconds(60)
    do {
        $taskJob = Invoke-TaskApi Get ('/api/jobs/' + $Id)
        if ($taskJob.status -in @('completed', 'partial', 'failed', 'cancelled')) { return $taskJob }
        if ([datetime]::UtcNow -gt $taskDeadline) { throw ('Job timed out: ' + $Id + ' / ' + $taskJob.stage) }
        Start-Sleep -Milliseconds 250
    } while ($true)
}

try {
    $taskOriginalStates = @{}
    foreach ($taskFixtureName in @('Commerce', 'Billing', 'MappingSample')) {
        $taskOriginal = Join-Path $taskRoot ('tests/Fixtures/' + $taskFixtureName)
        $taskOriginalStates[$taskFixtureName] = Get-TaskTreeState $taskOriginal -SourceFilesOnly
        Copy-TaskTree $taskOriginal (Join-Path $taskFixtureRoot $taskFixtureName)
    }
    Set-Content -LiteralPath (Join-Path $taskFixtureRoot 'WorkspaceSmoke.slnx') -Encoding utf8 -Value '<Solution><Project Path="MappingSample/MappingSample.csproj" /></Solution>'
    $taskBeforeFull = Get-TaskTreeState $taskFixtureRoot
    if (($taskDefaultExecutable -and -not $SkipBuild) -or -not (Test-Path -LiteralPath $Executable)) {
        & dotnet build (Join-Path $taskRoot 'src/Schemora.Host/Schemora.Host.csproj') -c Debug --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Debug Host build failed. Stop any known process using this exact build before retrying.' }
    }
    $Executable = (Resolve-Path -LiteralPath $Executable).Path
    $taskExecutableHash = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    Start-TaskHost

    $taskWorkspace = Invoke-TaskApi Post '/api/workspaces' @{ name = 'Workspace HTTP smoke'; roots = @($taskFixtureRoot); excludes = @('**/generated/**') }
    Assert-Task (-not [string]::IsNullOrWhiteSpace($taskWorkspace.id)) 'workspace-created' @{ roots = @($taskWorkspace.roots).Count }
    $taskFull = Start-TaskScan $taskWorkspace.id full
    $taskSse = Invoke-WebRequest ($script:taskBase + '/api/jobs/' + $taskFull.id + '/events') -Headers $script:taskHeaders -TimeoutSec 45
    Assert-Task ($taskSse.StatusCode -eq 200 -and $taskSse.Headers['Content-Type'] -match 'text/event-stream' -and $taskSse.Content -match 'event: progress\r?\ndata: ') 'job-sse-progress-endpoint' @{ events = [regex]::Matches($taskSse.Content, 'event: progress').Count }
    $taskFull = Wait-TaskJob $taskFull.id
    Assert-Task ($taskFull.status -in @('completed', 'partial')) 'full-scan-completed' @{ status = $taskFull.status; result = $taskFull.result }
    $taskCatalog = Invoke-TaskApi Get ('/api/workspaces/' + $taskWorkspace.id + '/catalog')
    Assert-Task (@($taskCatalog.databases).Count -eq 2) 'two-separate-databases' @{ databases = @($taskCatalog.databases | Select-Object name, id, status) }
    Assert-Task (@($taskCatalog.sources | Where-Object kind -eq 'solution').Count -eq 1 -and @($taskCatalog.sources | Where-Object kind -eq 'csharpProject').Count -eq 1) 'csharp-project-and-solution-discovered'
    $taskCommerce = @($taskCatalog.databases | Where-Object name -eq 'Commerce')[0]
    $taskBilling = @($taskCatalog.databases | Where-Object name -eq 'Billing')[0]
    $taskCommerceGraph = Invoke-TaskApi Get ('/api/databases/' + $taskCommerce.id + '/schema')
    $taskBillingGraph = Invoke-TaskApi Get ('/api/databases/' + $taskBilling.id + '/schema')
    Assert-Task (@($taskCommerceGraph.tables).Count -eq 4 -and @($taskBillingGraph.tables).Count -eq 2) 'graphs-have-real-fixture-tables' @{ Commerce = @($taskCommerceGraph.tables).Count; Billing = @($taskBillingGraph.tables).Count }
    $taskCommerceCustomer = @($taskCommerceGraph.tables | Where-Object { $_.schema -eq 'dbo' -and $_.name -eq 'Customers' })[0]
    $taskBillingCustomer = @($taskBillingGraph.tables | Where-Object { $_.schema -eq 'dbo' -and $_.name -eq 'Customers' })[0]
    Assert-Task ($taskCommerceCustomer.id -ne $taskBillingCustomer.id) 'same-name-tables-have-distinct-identity'
    Assert-Task ($taskBeforeFull -eq (Get-TaskTreeState $taskFixtureRoot)) 'full-scan-source-tree-unchanged'
    $taskBillingSnapshot = $taskBilling.currentSnapshotId
    $taskCommerceSnapshot = $taskCommerce.currentSnapshotId

    $taskQuick = Wait-TaskJob (Start-TaskScan $taskWorkspace.id quick).id
    Assert-Task ($taskQuick.status -in @('completed', 'partial') -and $taskQuick.result.databasesUpdated -eq 0 -and $taskQuick.result.changedSources -eq 0) 'unchanged-quick-refresh-reuses-snapshots' $taskQuick.result
    Assert-Task ($taskBeforeFull -eq (Get-TaskTreeState $taskFixtureRoot)) 'unchanged-quick-source-tree-unchanged'

    $taskCommerceSql = Join-Path $taskFixtureRoot 'Commerce/Schema.sql'
    Add-Content -LiteralPath $taskCommerceSql -Encoding utf8 -Value "`nCREATE TABLE [dbo].[SmokeAudit] ([AuditId] bigint NOT NULL PRIMARY KEY, [Note] nvarchar(100) NULL);`nGO"
    $taskBeforeChangedQuick = Get-TaskTreeState $taskFixtureRoot
    $taskChangedQuick = Wait-TaskJob (Start-TaskScan $taskWorkspace.id quick).id
    Assert-Task ($taskChangedQuick.status -in @('completed', 'partial') -and $taskChangedQuick.result.databasesUpdated -eq 1 -and $taskChangedQuick.result.changedSources -eq 1) 'changed-quick-updates-only-one-database' $taskChangedQuick.result
    $taskCatalog = Invoke-TaskApi Get ('/api/workspaces/' + $taskWorkspace.id + '/catalog')
    $taskCurrentCommerce = @($taskCatalog.databases | Where-Object id -eq $taskCommerce.id)[0]
    $taskCurrentBilling = @($taskCatalog.databases | Where-Object id -eq $taskBilling.id)[0]
    $taskChangedGraph = Invoke-TaskApi Get ('/api/databases/' + $taskCommerce.id + '/schema')
    Assert-Task (@($taskChangedGraph.tables).Count -eq 5 -and @($taskChangedGraph.tables | Where-Object name -eq 'SmokeAudit').Count -eq 1) 'changed-commerce-graph-adds-table' @{ tables = @($taskChangedGraph.tables).Count }
    Assert-Task ($taskCurrentCommerce.currentSnapshotId -ne $taskCommerceSnapshot -and $taskCurrentBilling.currentSnapshotId -eq $taskBillingSnapshot) 'unaffected-billing-snapshot-preserved'
    Assert-Task ($taskBeforeChangedQuick -eq (Get-TaskTreeState $taskFixtureRoot)) 'changed-quick-source-tree-unchanged'

    $taskBillingCopy = [IO.Path]::GetFullPath((Join-Path $taskFixtureRoot 'Billing'))
    $taskFixtureBoundary = [IO.Path]::GetFullPath($taskFixtureRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $taskBillingCopy.StartsWith($taskFixtureBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to delete a path outside this smoke fixture copy.' }
    Remove-Item -LiteralPath $taskBillingCopy -Recurse -Force
    $taskBeforeRemovalScan = Get-TaskTreeState $taskFixtureRoot
    $taskRemovedFull = Wait-TaskJob (Start-TaskScan $taskWorkspace.id full).id
    Assert-Task ($taskRemovedFull.status -in @('completed', 'partial')) 'full-rescan-after-removal-completed' $taskRemovedFull.result
    $taskCatalog = Invoke-TaskApi Get ('/api/workspaces/' + $taskWorkspace.id + '/catalog')
    Assert-Task (@($taskCatalog.databases).Count -eq 1 -and @($taskCatalog.databases | Where-Object id -eq $taskBilling.id).Count -eq 0) 'removed-project-leaves-current-catalog'
    $taskHistoricBilling = Invoke-TaskApi Get ('/api/schema/snapshots/' + $taskBillingSnapshot)
    Assert-Task (@($taskHistoricBilling.tables).Count -eq 2) 'removed-database-historical-snapshot-readable'
    Assert-Task ($taskBeforeRemovalScan -eq (Get-TaskTreeState $taskFixtureRoot)) 'removal-rescan-source-tree-unchanged'

    Stop-TaskHost
    Start-TaskHost
    $taskPersistedWorkspaces = @(Invoke-TaskApi Get '/api/workspaces')
    $taskPersistedCatalog = Invoke-TaskApi Get ('/api/workspaces/' + $taskWorkspace.id + '/catalog')
    $taskPersistedGraph = Invoke-TaskApi Get ('/api/databases/' + $taskCommerce.id + '/schema')
    $taskPersistedBilling = Invoke-TaskApi Get ('/api/schema/snapshots/' + $taskBillingSnapshot)
    $taskPersistedSnapshots = @(Invoke-TaskApi Get ('/api/databases/' + $taskCommerce.id + '/snapshots'))
    Assert-Task (@($taskPersistedWorkspaces | Where-Object id -eq $taskWorkspace.id).Count -eq 1 -and @($taskPersistedCatalog.databases).Count -eq 1) 'workspace-catalog-survives-host-restart'
    Assert-Task (@($taskPersistedGraph.tables).Count -eq 5 -and @($taskPersistedBilling.tables).Count -eq 2 -and $taskPersistedSnapshots.Count -ge 2) 'current-and-historical-snapshots-survive-restart' @{ CommerceSnapshots = $taskPersistedSnapshots.Count; CommerceTables = @($taskPersistedGraph.tables).Count; historicalBillingTables = @($taskPersistedBilling.tables).Count }
    Assert-Task ($taskBeforeRemovalScan -eq (Get-TaskTreeState $taskFixtureRoot)) 'restart-read-apis-source-tree-unchanged'
    foreach ($taskFixtureName in @('Commerce', 'Billing', 'MappingSample')) {
        Assert-Task ($taskOriginalStates[$taskFixtureName] -eq (Get-TaskTreeState (Join-Path $taskRoot ('tests/Fixtures/' + $taskFixtureName)) -SourceFilesOnly)) ('original-fixture-unchanged-' + $taskFixtureName)
    }
    $taskSuccess = $true
} catch {
    $taskFailure = $_.Exception.Message
    throw
} finally {
    Stop-TaskHost
    [ordered]@{
        success = $taskSuccess
        startedAt = $taskStartedAt.ToString('O')
        finishedAt = [datetime]::UtcNow.ToString('O')
        executable = $Executable
        executableSha256 = $taskExecutableHash
        fixtureCopy = [IO.Path]::GetRelativePath($taskRoot, $taskFixtureRoot)
        applicationData = [IO.Path]::GetRelativePath($taskRoot, $taskDataDirectory)
        tests = $taskChecks.ToArray()
        error = $taskFailure
        visualUiTested = $false
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $taskReportPath -Encoding utf8
}
Write-Output ('PASS: Workspace HTTP smoke (' + $taskChecks.Count + ' checks); report work/workspace-report.json')


