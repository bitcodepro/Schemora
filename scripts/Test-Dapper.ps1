[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') --nologo;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/dapper-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
'CREATE TABLE dbo.Transactions(TransactionId nvarchar(80) NOT NULL PRIMARY KEY);CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY,TransactionId nvarchar(80) NULL);CREATE TABLE dbo.RawEvents(RowId int NOT NULL PRIMARY KEY,OtherValue nvarchar(80) NULL);'|Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql')
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'|Set-Content -LiteralPath (Join-Path $fixture 'Mapping.csproj')
@'
using Dapper;
class Repository {
    const string JoinSql="SELECT d.DocumentId FROM dbo.Documents d JOIN dbo.Transactions t ON d.TransactionId=t.TransactionId";
    object Load(dynamic connection) => connection.Query(JoinSql,new { SecretParameter="DAPPER_PARAM_SENTINEL_DO_NOT_INDEX" });
    object Multi(dynamic connection) => connection.Query<object,object,object>("SELECT DocumentId FROM dbo.Documents",splitOn:"OtherValue");
    object Dynamic(dynamic connection,string table) => connection.Query($"SELECT * FROM {table}");
}
'@|Set-Content -LiteralPath (Join-Path $fixture 'Repository.cs')
$server=$null;$checks=0
try{
    $server=Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
    $workspace=Invoke-Schemora $server '/api/workspaces' POST @{name='Dapper acceptance';roots=@($fixture);excludes=@()}
    $base='/api/workspaces/'+$workspace.id
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
    $graph=Invoke-Schemora $server ($base+'/graph?limit=100&kind=embedded-sql-candidate')
    $edge=@($graph.relations|Where-Object {@($_.evidence|Where-Object source -eq 'dapper-sql').Count -gt 0})[0]
    Assert-Schemora ($null -ne $edge -and $edge.kind -eq 'embedded-sql-candidate' -and -not $edge.canUseForJoin) 'C# SQL evidence missing/unexpected trust';$checks++
    Assert-Schemora (@($edge.evidence|Where-Object {$_.source -eq 'dapper-sql' -and $_.path.EndsWith('Repository.cs') -and $_.line -gt 0}).Count -gt 0) 'Original C# evidence location missing';$checks++
    Assert-Schemora (@($graph.relations|Where-Object kind -eq 'declared-fk').Count -eq 0) 'Dapper created physical FK';$checks++
    $raw=@($graph.tables|Where-Object name -eq 'RawEvents')[0]
    Assert-Schemora (@($graph.relations|Where-Object {$_.fromTableId -eq $raw.id -or $_.toTableId -eq $raw.id}).Count -eq 0) 'splitOn invented relation';$checks++
    Assert-Schemora (@($graph.diagnostics|Where-Object code -eq 'DAPPER_SQL_DYNAMIC').Count -gt 0) 'Dynamic SQL not diagnosed';$checks++
    Assert-Schemora (-not ($graph|ConvertTo-Json -Depth 30).Contains('DAPPER_PARAM_SENTINEL_DO_NOT_INDEX')) 'Parameter value leaked into evidence';$checks++
    $paths=@(Invoke-Schemora $server ($base+'/paths') POST @{fromTableId=$edge.fromTableId;toTableId=$edge.toTableId})
    Assert-Schemora ($paths.Count -eq 0) 'Dapper SQL automatically trusted';$checks++
    Invoke-Schemora $server ($base+'/relations/'+$edge.id+'/decision') POST @{decision='confirmed';reason='Synthetic exact text ID relation verified'}|Out-Null
    $paths=@(Invoke-Schemora $server ($base+'/paths') POST @{fromTableId=$edge.fromTableId;toTableId=$edge.toTableId})
    Assert-Schemora ($paths.Count -eq 1 -and $paths[0].sql.Contains('INNER JOIN')) 'Confirmed C# SQL relation unavailable';$checks++
    [ordered]@{success=$true;checks=$checks;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/dapper-report.json')
    Write-Host "PASS $checks Dapper HTTP checks"
}finally{Stop-SchemoraTestHost $server}


