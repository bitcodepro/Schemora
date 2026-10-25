[CmdletBinding()]
param([string]$Executable,[switch]$Run)
$ErrorActionPreference='Stop';if(-not $Run){throw 'Explicit -Run is required for the synthetic Docker failure test.'}
$root=Split-Path $PSScriptRoot -Parent;. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $Executable){$Executable=Join-Path $root 'artifacts/win-x64/Schemora.Host.exe'}
$found=Get-Command docker -ErrorAction SilentlyContinue;$real=if($found){$found.Source}else{Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/resources/bin/docker.exe'};if(-not(Test-Path -LiteralPath $real -PathType Leaf)){throw 'Docker CLI unavailable; add it to PATH or install Docker Desktop.'};$runDirectory=Join-Path $root ('work/docker-failure-'+[guid]::NewGuid().ToString('N'));$shim=Join-Path $runDirectory 'shim';New-Item -ItemType Directory -Path $runDirectory|Out-Null
dotnet publish -m:1 (Join-Path $root 'tests/Tools/DockerFailureShim/DockerFailureShim.csproj') -c Release -o $shim --nologo | Out-Null
if($LASTEXITCODE -ne 0){throw 'Fault-injection shim build failed'}
$fixture=Join-Path $root 'tests/Fixtures/Commerce/bin/Debug/Commerce.dacpac'
if(-not (Test-Path -LiteralPath $fixture)){dotnet build -m:1 (Join-Path $root 'tests/Fixtures/Commerce/Commerce.sqlproj') -c Debug --nologo|Out-Null;if($LASTEXITCODE -ne 0){throw 'Fixture build failed'}}
$server=$null;$priorPath=$env:Path;$priorDocker=$env:SCHEMORA_TEST_REAL_DOCKER;$data=Join-Path $runDirectory 'data';$checks=0
try{
 $before=@(& $real ps -a --filter label=com.schemora.purpose=schema-preview --format '{{.ID}}') -join '|'
 $env:SCHEMORA_TEST_REAL_DOCKER=$real;$env:Path=$shim+[IO.Path]::PathSeparator+$priorPath
 $server=Start-SchemoraTestHost $Executable $runDirectory $data
 try{Invoke-Schemora $server '/api/schema/docker' POST @{path=$fixture;allowIsolatedDocker=$true}|Out-Null;throw 'Expected a deployment failure'}catch{if($_.ErrorDetails.Message -notmatch 'DOCKER_SCHEMA_PUBLISH_FAILED'){throw};$body=$_.ErrorDetails.Message|ConvertFrom-Json}
 $detail=$body.detail|ConvertFrom-Json
 Assert-Schemora ($detail.stage -eq 'schema-publish' -and $detail.exitCode -eq 1 -and $detail.elapsedMs -gt 0) 'Failure lost its stage, duration or exit code';$checks++
 Assert-Schemora ($detail.output.Contains('synthetic deployment error 102') -and $detail.output.Contains('[REDACTED]') -and $detail.output -notmatch 'ScH9!') 'Failure output or password redaction failed';$checks++
 $reports=@(Invoke-Schemora $server '/api/diagnostics/docker');Assert-Schemora ($reports.Count -eq 1 -and $reports[0].id -eq $detail.id) 'Persisted Docker report missing';$checks++
 Assert-Schemora ((Get-ChildItem -LiteralPath (Join-Path $data 'docker-runs') -Directory).Count -eq 0) 'Private schema staging survived failed deployment';$checks++
 $after=@(& $real ps -a --filter label=com.schemora.purpose=schema-preview --format '{{.ID}}') -join '|'
 Assert-Schemora ($before -eq $after) 'Fault injection left owned Docker resources';$checks++
 [ordered]@{success=$true;checks=$checks;faultInjected=$true;realDockerLifecycle=$true;runDirectory=$runDirectory}|ConvertTo-Json|Set-Content (Join-Path $root 'work/docker-failure-report.json')
 Write-Output ('PASS '+$checks+' injected Docker failure checks')
}finally{Stop-SchemoraTestHost $server;$env:Path=$priorPath;$env:SCHEMORA_TEST_REAL_DOCKER=$priorDocker}
