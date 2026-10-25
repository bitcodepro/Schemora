[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild,[string]$ChromePath='C:\Program Files\Google\Chrome\Application\chrome.exe')
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -c Release --nologo -m:1;if($LASTEXITCODE -ne 0){throw 'Host build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Release/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/product-v13-ui-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture';$data=Join-Path $run 'data';$screenshots=Join-Path $run 'screenshots'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
'CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY); CREATE TABLE dbo.Audits(AuditId int NOT NULL PRIMARY KEY,DocumentId int NOT NULL);'|Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql')
$project=Join-Path $fixture 'SyntheticContext';New-Item -ItemType Directory -Path $project -Force|Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Base.cs"/><Compile Include="ReleaseOnly.cs" Condition="'$(Configuration)'=='Release'"/></ItemGroup></Project>
'@|Set-Content -LiteralPath (Join-Path $project 'SyntheticContext.csproj')
'namespace Artificial; public sealed class BaseEntity { public int Id { get; set; } }'|Set-Content -LiteralPath (Join-Path $project 'Base.cs')
'namespace Artificial; public sealed class ReleaseOnlyEntity { public int Id { get; set; } }'|Set-Content -LiteralPath (Join-Path $project 'ReleaseOnly.cs')
$report=Join-Path $root 'work/product-v13-ui-report.json';$server=$null
try{
    $server=Start-SchemoraTestHost $Executable $run $data
    & node (Join-Path $root 'ui/e2e/product-v13.mjs') $server.SessionPath $fixture $report $screenshots $ChromePath
    if($LASTEXITCODE -ne 0){throw 'Product v1.3 browser checks failed'}
    $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
    Assert-Schemora $result.success 'Product v1.3 browser report failed'
    Write-Host ('PASS '+$result.checks.Count+' product v1.3 Chrome workflow checks.')
}finally{Stop-SchemoraTestHost $server}
