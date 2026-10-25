[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $Executable){$Executable=Join-Path $root 'artifacts/win-x64/Schemora.Host.exe'}
$run=Join-Path $root ('work/professional-ui-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $run|Out-Null;$server=$null
try{$server=Start-SchemoraTestHost $Executable $run (Join-Path $run 'data');& node (Join-Path $root 'ui/e2e/professional.mjs') $server.SessionPath (Join-Path $root 'work/professional-ui-report.json');if($LASTEXITCODE -ne 0){throw 'Professional browser checks failed'}}finally{Stop-SchemoraTestHost $server}
