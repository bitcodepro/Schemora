[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -m:1 --nologo;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/maintenance-v13-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
$ddl=[Text.StringBuilder]::new();[void]$ddl.AppendLine('CREATE TABLE dbo.Hub(Id int NOT NULL PRIMARY KEY);')
for($index=1;$index -le 350;$index++){[void]$ddl.AppendLine(('CREATE TABLE dbo.Leaf{0:D3}(Id int NOT NULL PRIMARY KEY,HubId int NOT NULL,CONSTRAINT FK_Leaf{0:D3} FOREIGN KEY(HubId) REFERENCES dbo.Hub(Id));' -f $index))}
$ddl.ToString()|Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql')
$server=$null;$checks=0
try {
 $server=Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
 $workspace=Invoke-Schemora $server '/api/workspaces' POST @{name='Maintenance acceptance';roots=@($fixture);excludes=@()};$base='/api/workspaces/'+$workspace.id
 $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
 $policy=Invoke-Schemora $server '/api/storage/policy';Assert-Schemora $policy.lowMemory 'Low memory is not the default';$checks++
 $graph=Invoke-Schemora $server ($base+'/graph?limit=150');Assert-Schemora ($graph.tables.Count -eq 150 -and $graph.totalTables -eq 351 -and $graph.truncated) 'Graph is not bounded';$checks++
 $first=Invoke-Schemora $server ($base+'/relations/page?limit=100&offset=0');$second=Invoke-Schemora $server ($base+'/relations/page?limit=100&offset=100')
 Assert-Schemora ($first.total -eq 350 -and $first.items.Count -eq 100 -and $second.items.Count -eq 100) 'Relation pages not bounded';$checks++
 Assert-Schemora (@($first.items.id|Where-Object {$_ -in $second.items.id}).Count -eq 0) 'Relation pages overlap';$checks++
 $search=Invoke-Schemora $server ($base+'/relations/page?query=Leaf350');Assert-Schemora ($search.total -eq 1) 'Whole-catalog relation search failed';$checks++
 $hub=Invoke-Schemora $server ($base+'/tables?query=dbo.Hub');$focus=[uri]::EscapeDataString($hub.items[0].id)
 $near=Invoke-Schemora $server ($base+'/graph?focusTableId='+$focus+'&hops=1&limit=20');Assert-Schemora ($near.totalTables -eq 351 -and $near.tables.Count -eq 20) 'Indexed neighborhood failed';$checks++
 $objects=Invoke-Schemora $server ($base+'/objects?limit=7&offset=340');Assert-Schemora ($objects.total -eq 351 -and $objects.items.Count -eq 7) 'SQL object paging failed';$checks++
 $log=@(Invoke-Schemora $server ('/api/jobs/'+$job.id+'/log'));Assert-Schemora ($log.Count -gt 1 -and (@($log.code|Where-Object {$_ -in @('JOB_COMPLETED','JOB_PARTIAL')}).Count -gt 0)) 'Job diagnostic events missing';$checks++
 $bad=Invoke-WebRequest -Uri ($server.Origin+'/api/source/open') -Method POST -Headers $server.Headers -ContentType 'application/json' -Body '{"path":"relative.sql","editor":"vscode"}' -SkipHttpErrorCheck
 $failure=$bad.Content|ConvertFrom-Json;Assert-Schemora ($bad.StatusCode -eq 400 -and $failure.code -eq 'SOURCE_PATH_INVALID' -and $failure.operationId.Length -eq 32) 'Structured errors lost code or operation';$checks++
 $report=Invoke-Schemora $server '/api/diagnostics/report' POST @{includePaths=$false};$json=$report|ConvertTo-Json -Depth 40
 Assert-Schemora (-not $report.pathsIncluded -and $null -eq $report.paths -and -not $json.Contains($fixture) -and -not $json.Contains($server.Headers['X-Schemora-Token'])) 'Default report leaks paths/token';$checks++
 $withPaths=Invoke-Schemora $server '/api/diagnostics/report' POST @{includePaths=$true};Assert-Schemora ($withPaths.paths -contains $fixture) 'Explicit path opt-in failed';$checks++
 $plan=Invoke-Schemora $server '/api/storage/cleanup/preview' POST @{};Assert-Schemora ($plan.snapshots -eq 0 -and $plan.protectedSnapshotIds.Count -gt 0) 'Cleanup does not protect current snapshot';$checks++
 $size=Invoke-Schemora $server '/api/storage/size';Assert-Schemora ($size.catalogBytes -gt 0 -and $size.tables.Count -gt 2) 'Storage size is missing';$checks++
 $changed=Invoke-Schemora $server '/api/storage/policy' PUT @{lowMemory=$false;keepSnapshotsPerDatabase=3;keepHistoryDays=5};Assert-Schemora (-not $changed.lowMemory) 'Policy save failed';$checks++
 [ordered]@{success=$true;checks=$checks;relations=350;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/maintenance-v13-report.json')
 Write-Host "PASS $checks maintenance/diagnostics HTTP checks"
} finally {Stop-SchemoraTestHost $server}
