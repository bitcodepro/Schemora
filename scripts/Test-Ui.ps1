[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild,[string]$ChromePath='C:\Program Files\Google\Chrome\Application\chrome.exe')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild) { & dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -c Debug --nologo; if($LASTEXITCODE -ne 0){throw 'Host build failed'} }
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
if(-not(Test-Path -LiteralPath $Executable)){throw 'Host executable is missing'}
if(-not(Test-Path -LiteralPath $ChromePath)){throw 'Installed Chrome is missing; pass -ChromePath explicitly'}
$run=Join-Path $root ('work/ui-'+[guid]::NewGuid().ToString('N'))
$fixture=Join-Path $run 'fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
# Copy only this build output, so the test never holds the repository Host executable open.
Copy-Item -LiteralPath (Split-Path $Executable -Parent) -Destination (Join-Path $run 'host-output') -Recurse -Force
$testExecutable=Join-Path (Join-Path $run 'host-output') (Split-Path $Executable -Leaf)
'<Project><Sdk Name="Microsoft.Build.Sql" Version="2.3.0"/><PropertyGroup><Name>UiDemo</Name><DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP></PropertyGroup></Project>' | Set-Content -LiteralPath (Join-Path $fixture 'UiDemo.sqlproj') -Encoding utf8
$schema="CREATE TABLE dbo.Accounts(AccountId int NOT NULL PRIMARY KEY,Name nvarchar(100) NOT NULL);`nCREATE TABLE dbo.Transactions(TransactionId int NOT NULL PRIMARY KEY,AccountId int NOT NULL,ExternalId int NOT NULL,CONSTRAINT FK_Transactions_Accounts FOREIGN KEY(AccountId) REFERENCES dbo.Accounts(AccountId));"
$schema | Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql') -Encoding utf8
$enhancedFixture=Join-Path $run 'enhancement-fixture'
New-Item -ItemType Directory -Path $enhancedFixture -Force | Out-Null
'<Project><Sdk Name="Microsoft.Build.Sql" Version="2.3.0"/><PropertyGroup><Name>UiCommerce</Name><DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP></PropertyGroup></Project>' | Set-Content -LiteralPath (Join-Path $enhancedFixture 'UiCommerce.sqlproj') -Encoding utf8
$extendedSchema=@"
CREATE TABLE dbo.Accounts(TenantId int NOT NULL,AccountId int NOT NULL,Name nvarchar(100) NOT NULL,CONSTRAINT PK_Accounts PRIMARY KEY(TenantId,AccountId));
GO
CREATE TABLE dbo.Transactions(TransactionId int NOT NULL PRIMARY KEY,TenantId int NOT NULL,AccountId int NOT NULL,ExternalId int NOT NULL,CONSTRAINT FK_Transactions_Accounts FOREIGN KEY(TenantId,AccountId) REFERENCES dbo.Accounts(TenantId,AccountId));
GO
CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY,TenantId int NOT NULL,AccountId int NOT NULL);
GO
CREATE VIEW dbo.vAccounts AS SELECT TenantId,AccountId,Name FROM dbo.Accounts;
GO
CREATE PROCEDURE dbo.GetAccounts @TenantId int,@Count int OUTPUT AS BEGIN SELECT * FROM dbo.Accounts WHERE TenantId=@TenantId; SET @Count=1; END;
GO
CREATE FUNCTION dbo.GetLabel(@AccountId int) RETURNS nvarchar(100) AS BEGIN RETURN N'label'; END;
GO
CREATE FUNCTION dbo.ListAccounts(@TenantId int) RETURNS TABLE AS RETURN(SELECT TenantId,AccountId,Name FROM dbo.Accounts WHERE TenantId=@TenantId);
GO
CREATE SYNONYM dbo.AccountAlias FOR dbo.Accounts;
GO
CREATE NONCLUSTERED INDEX IX_Transactions_ExternalId ON dbo.Transactions(ExternalId DESC) INCLUDE(TransactionId) WHERE ExternalId>0;
"@
$extendedSchema | Set-Content -LiteralPath (Join-Path $enhancedFixture 'Schema.sql') -Encoding utf8
$server=$null
try {
    $server=Start-SchemoraTestHost $testExecutable $run (Join-Path $run 'data')
    & node (Join-Path $root 'ui/e2e/smoke.mjs') $server.SessionPath $fixture (Join-Path $root 'work/ui-report.json') (Join-Path $run 'screenshots') $ChromePath
    if($LASTEXITCODE -ne 0){throw 'Browser acceptance failed; see work/ui-report.json and synthetic screenshots'}
    & node (Join-Path $root 'ui/e2e/enhancements.mjs') $server.SessionPath $enhancedFixture (Join-Path $root 'work/ui-report.json') (Join-Path $run 'screenshots') $ChromePath
    if($LASTEXITCODE -ne 0){throw 'Enhanced browser acceptance failed; see work/ui-report.json'} 
    $report=Get-Content -LiteralPath (Join-Path $root 'work/ui-report.json') -Raw | ConvertFrom-Json
    Assert-Schemora $report.success 'UI report did not confirm success'
    Write-Host ('UI acceptance passed: '+$report.checks.Count+' checks. Report: work/ui-report.json')
} finally {
    Stop-SchemoraTestHost $server
    $copyPath=[IO.Path]::GetFullPath((Join-Path $run 'host-output'))
    $runPrefix=[IO.Path]::GetFullPath($run)+[IO.Path]::DirectorySeparatorChar
    if($copyPath.StartsWith($runPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $copyPath -Leaf) -eq 'host-output' -and (Test-Path -LiteralPath $copyPath)){Remove-Item -LiteralPath $copyPath -Recurse -Force}
}


