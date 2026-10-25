[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild,[string]$ChromePath='C:\Program Files\Google\Chrome\Application\chrome.exe')
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -c Release --nologo;if($LASTEXITCODE -ne 0){throw 'Host build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Release/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/localization-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture';$data=Join-Path $run 'data';$screenshots=Join-Path $run 'screenshots'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
@'
CREATE TABLE dbo.Accounts(AccountId int NOT NULL PRIMARY KEY,Name nvarchar(100) NOT NULL);
GO
CREATE TABLE dbo.Transactions(TransactionId int NOT NULL PRIMARY KEY,AccountId int NOT NULL,ExternalId int NOT NULL,CONSTRAINT FK_Transactions_Accounts FOREIGN KEY(AccountId) REFERENCES dbo.Accounts(AccountId));
'@|Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql') -Encoding utf8
$report=Join-Path $root 'work/localization-report.json';$server=$null
try{
    $server=Start-SchemoraTestHost $Executable $run $data
    & node (Join-Path $root 'ui/e2e/localization.mjs') $server.SessionPath $fixture $report $screenshots $ChromePath full
    if($LASTEXITCODE -ne 0){throw 'Localization browser checks failed'}
    Stop-SchemoraTestHost $server;$server=$null
    $server=Start-SchemoraTestHost $Executable $run $data
    & node (Join-Path $root 'ui/e2e/localization.mjs') $server.SessionPath $fixture $report $screenshots $ChromePath persist
    if($LASTEXITCODE -ne 0){throw 'Language restart persistence checks failed'}
    $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
    Assert-Schemora $result.success 'Localization report failed'
    Write-Host ('PASS '+$result.checks.Count+' localization Chrome checks, including host restart.')
}finally{Stop-SchemoraTestHost $server}
