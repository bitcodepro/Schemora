[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -m:1;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'artifacts/win-x64/Schemora.Host.exe'}
$run=Join-Path $root ('work/experience-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $run|Out-Null
$server=$null
try{
 $server=Start-SchemoraTestHost (Resolve-Path -LiteralPath $Executable).Path $run (Join-Path $run 'data')
 & node (Join-Path $root 'ui/e2e/experience.mjs') $server.SessionPath (Join-Path $root 'work/experience-report.json')
 if($LASTEXITCODE -ne 0){throw 'Experience browser acceptance failed'}
 & node (Join-Path $root 'ui/e2e/authoring-v15.mjs') $server.SessionPath (Join-Path $root 'work/authoring-v15-report.json')
 if($LASTEXITCODE -ne 0){throw 'Authoring v1.5 browser acceptance failed'}
 & node (Join-Path $root 'ui/e2e/state-regressions.mjs') $server.SessionPath (Join-Path $root 'work/state-regressions-report.json')
 if($LASTEXITCODE -ne 0){throw 'Navigation/schema/palette regression acceptance failed'}
 Write-Output 'PASS tutorial/navigation/dependency/HTML/filter/keyboard/zoom Chrome acceptance'
}finally{Stop-SchemoraTestHost $server}
