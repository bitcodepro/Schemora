# Shared helpers for repository-owned, synthetic local integration tests.
$ErrorActionPreference = 'Stop'
function Assert-Schemora([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Start-SchemoraTestHost([string]$Executable,[string]$RunDirectory,[string]$DataDirectory,[string[]]$AdditionalArguments=@()) {
    $sessionPath = Join-Path $RunDirectory ('session-' + [guid]::NewGuid().ToString('N') + '.json')
    $process = [Diagnostics.Process]::new(); $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $process.StartInfo.UseShellExecute = $false; $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.WorkingDirectory = $RunDirectory; $process.StartInfo.RedirectStandardOutput = $true; $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in (@('--no-browser','--session-file',$sessionPath,'--data-dir',$DataDirectory)+$AdditionalArguments)) { $process.StartInfo.ArgumentList.Add($argument) }
    [void]$process.Start(); $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [datetime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $sessionPath)) {
        if ($process.HasExited) { throw ('Host exited: ' + $stdout.Result + $stderr.Result) }
        if ([datetime]::UtcNow -gt $deadline) { $process.Kill($true); throw 'Host startup timed out' }
        Start-Sleep -Milliseconds 100
    }
    $session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{ Process = $process; Origin = $session.origin; Headers = @{ 'X-Schemora-Token' = $session.token; Origin = $session.origin }; SessionPath = $sessionPath; Stdout = $stdout; Stderr = $stderr }
}
function Stop-SchemoraTestHost($Server) {
    if ($null -eq $Server) { return }
    if (-not $Server.Process.HasExited) { $Server.Process.Kill($true); $Server.Process.WaitForExit() }
    $Server.Process.Dispose()
    if (Test-Path -LiteralPath $Server.SessionPath) { Remove-Item -LiteralPath $Server.SessionPath -Force }
}
function Invoke-Schemora($Server,[string]$Path,[string]$Method='GET',$Body=$null) {
    $parameters = @{ Uri = $Server.Origin + $Path; Headers = $Server.Headers; Method = $Method; TimeoutSec = 180 }
    if ($null -ne $Body) { $parameters.ContentType = 'application/json'; $parameters.Body = $Body | ConvertTo-Json -Depth 40 -Compress }
    $response = Invoke-RestMethod @parameters
    foreach ($item in $response) { $item }
}
function Wait-SchemoraJob($Server,[string]$Id) {
    $deadline = [datetime]::UtcNow.AddMinutes(6)
    do {
        $job = Invoke-Schemora $Server ('/api/jobs/' + $Id)
        if ($job.status -notin @('queued','running')) {
            if ($job.status -notin @('completed','partial')) { throw ('Job ' + $job.status + ': ' + $job.error) }
            return $job
        }
        if ([datetime]::UtcNow -gt $deadline) { throw 'Job timeout' }
        Start-Sleep -Milliseconds 150
    } while ($true)
}





# Each test run leaves a work/<prefix>-<guid> folder for its reports and screenshots; keep only the newest few per prefix.
function Remove-OldTestRuns([string]$WorkDirectory,[int]$Keep=3,[switch]$WhatIf) {
    if (-not (Test-Path -LiteralPath $WorkDirectory)) { return }
    $runs = @(Get-ChildItem -LiteralPath $WorkDirectory -Directory | Where-Object { $_.Name -match '-[0-9a-f]{32}$' })
    foreach ($group in ($runs | Group-Object { $_.Name -replace '-[0-9a-f]{32}$', '' })) {
        foreach ($old in ($group.Group | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip $Keep)) {
            if ($WhatIf) { Write-Output ('Would remove ' + $old.FullName) } else { Remove-Item -LiteralPath $old.FullName -Recurse -Force }
        }
    }
}
