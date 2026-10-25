[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild,[ValidateRange(100,10000)][int]$Tables=5000)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') --nologo;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/scale-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
$ddl=[Text.StringBuilder]::new()
for($index=1;$index -le $Tables;$index++){[void]$ddl.AppendLine(('CREATE TABLE dbo.Table{0:D5}(Id int NOT NULL PRIMARY KEY,Payload nvarchar(80) NULL);' -f $index))}
$ddl.ToString()|Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql')
$server=$null
try{
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $server=Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
    $startup=$watch.ElapsedMilliseconds;$watch.Restart()
    $workspace=Invoke-Schemora $server '/api/workspaces' POST @{name='Scale acceptance';roots=@($fixture);excludes=@()};$base='/api/workspaces/'+$workspace.id
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
    $scan=$watch.ElapsedMilliseconds;$watch.Restart()
    $graph=Invoke-Schemora $server ($base+'/graph?limit=150');$graphTime=$watch.ElapsedMilliseconds
    Assert-Schemora ($graph.totalTables -eq $Tables -and $graph.tables.Count -eq 150 -and $graph.truncated) 'Large graph not bounded'
    $last='Table{0:D5}' -f $Tables
    $search=Invoke-Schemora $server ($base+'/tables?query='+$last+'&limit=20')
    Assert-Schemora ($search.total -eq 1 -and $search.items[0].name -eq $last) 'Whole catalog search failed'
    $watch.Restart();$job=Invoke-Schemora $server ($base+'/scan') POST @{mode='quick'};$quick=Wait-SchemoraJob $server $job.id;$quickTime=$watch.ElapsedMilliseconds
    Assert-Schemora ($quick.result.changedSources -eq 0 -and $quick.result.databasesUpdated -eq 0) 'Unchanged quick rebuilds schema'
    $focus=$graph.tables[0].id
    Assert-Schemora (-not [string]::IsNullOrWhiteSpace($focus)) 'Graph response has no focus object identifier'
    $watch.Restart();$coldIndex=Invoke-Schemora $server ($base+'/dependency-paths?objectId='+[uri]::EscapeDataString($focus));$coldIndexMs=$watch.ElapsedMilliseconds
    $watch.Restart();$warmIndex=Invoke-Schemora $server ($base+'/dependency-paths?objectId='+[uri]::EscapeDataString($focus));$warmIndexMs=$watch.ElapsedMilliseconds
    Assert-Schemora ($coldIndex.refreshedDatabases -eq 1 -and $warmIndex.refreshedDatabases -eq 0 -and $warmIndex.updatedNodes -eq 0 -and $warmIndex.nodes.Count -eq 1) 'Dependency projection was not reused or bounded'
    $server.Process.Refresh()
    [ordered]@{success=$true;tables=$Tables;startupMs=$startup;scanMs=$scan;graphMs=$graphTime;quickMs=$quickTime;hostWorkingSetMiB=[math]::Round($server.Process.WorkingSet64/1MB,1);graphNodes=$graph.tables.Count;dependencyColdMs=$coldIndexMs;dependencyWarmMs=$warmIndexMs;dependencyWarmRefreshedDatabases=$warmIndex.refreshedDatabases;dependencyResponseNodes=$warmIndex.nodes.Count;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/scale-report.json')
    Write-Host "PASS $Tables tables; startup $startup ms scan $scan ms graph $graphTime ms quick $quickTime ms; 150 nodes"
}finally{Stop-SchemoraTestHost $server}

