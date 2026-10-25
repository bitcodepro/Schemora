[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') --nologo;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/changes-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$sql=Join-Path $fixture 'Schema.sql'
'CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY,OldCode nvarchar(80) NULL);'|Set-Content -LiteralPath $sql
$server=$null;$checks=0
try{
    $server=Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
    $workspace=Invoke-Schemora $server '/api/workspaces' POST @{name='Changes acceptance';roots=@($fixture);excludes=@()}
    $base='/api/workspaces/'+$workspace.id
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
    $catalog=Invoke-Schemora $server ($base+'/catalog');$db=$catalog.databases[0];$before=$db.currentSnapshotId
    $graph=Invoke-Schemora $server ('/api/databases/'+$db.id+'/schema');$table=$graph.tables[0]
    $definition=@{id='x';name='Code consumer';description='Only generated fixtures';tags=@('impact');dialect='sqlserver';databaseIds=@($db.id);version=0;sqlTemplate="SELECT d.OldCode FROM dbo.Documents d WHERE d.DocumentId IN ({{ids:Ids}}); -- OldCode literal remains";inputs=@(@{name='Ids';type='int'});compatibilityLevel=130}
    $affected=Invoke-Schemora $server ($base+'/recipes') POST $definition
    $definition.name='ID consumer';$definition.sqlTemplate='SELECT d.DocumentId FROM dbo.Documents d WHERE d.DocumentId IN ({{ids:Ids}});'
    $unaffected=Invoke-Schemora $server ($base+'/recipes') POST $definition
    Assert-Schemora ($affected.validation.status -eq 'Valid' -and $unaffected.validation.status -eq 'Valid') 'Initial recipes not valid';$checks++
    'CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY,NewCode nvarchar(80) NULL);'|Set-Content -LiteralPath $sql
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='quick'};$done=Wait-SchemoraJob $server $job.id
    Assert-Schemora ($done.result.recipesChecked -eq 2) 'Scan did not revalidate recipes';$checks++
    $catalog=Invoke-Schemora $server ($base+'/catalog');$after=$catalog.databases[0].currentSnapshotId
    $diff=Invoke-Schemora $server ('/api/databases/'+$db.id+'/diff?from='+$before+'&to='+$after)
    Assert-Schemora (@($diff.changes|Where-Object {$_.column -eq 'OldCode'}).Count -gt 0 -and @($diff.changes|Where-Object {$_.column -eq 'NewCode'}).Count -gt 0) 'Column diff missing';$checks++
    Assert-Schemora (@($diff.renameCandidates|Where-Object {$_.oldName -eq 'OldCode' -and $_.newName -eq 'NewCode'}).Count -eq 1) 'Rename candidate missing';$checks++
    $impact=@(Invoke-Schemora $server ($base+'/impact'))
    Assert-Schemora ((@($impact|Where-Object recipeId -eq $affected.definition.id)[0]).status -eq 'Broken') 'Affected recipe not Broken';$checks++
    Assert-Schemora ((@($impact|Where-Object recipeId -eq $unaffected.definition.id)[0]).status -eq 'Valid') 'Unrelated recipe changed status';$checks++
    $id=$affected.definition.id
    $proposal=Invoke-Schemora $server ('/api/recipes/'+$id+'/repair-proposals') POST @{databaseId=$db.id;tableId=$table.id;oldColumn='OldCode';newColumn='NewCode';reason='Synthetic explicit rename verified';expectedVersion=1}
    Assert-Schemora ($proposal.status -eq 'proposed' -and $proposal.beforeSql.Contains('d.OldCode') -and $proposal.afterSql.Contains('d.[NewCode]') -and $proposal.afterSql.Contains('-- OldCode literal remains')) 'Safe AST preview failed';$checks++
    $still=Invoke-Schemora $server ('/api/recipes/'+$id)
    Assert-Schemora ($still.definition.version -eq 1 -and $still.definition.sqlTemplate.Contains('d.OldCode')) 'Proposal silently changed recipe';$checks++
    $approved=Invoke-Schemora $server ('/api/repair-proposals/'+$proposal.id+'/approve') POST @{expectedVersion=1}
    Assert-Schemora ($approved.definition.version -eq 2 -and $approved.validation.status -eq 'Valid') 'Approval did not create usable new version';$checks++
    $history=@(Invoke-Schemora $server ('/api/recipes/'+$id+'/repair-proposals'))
    Assert-Schemora ($history[0].status -eq 'approved' -and $history[0].appliedVersion -eq 2) 'Approval metadata missing';$checks++
    $rolled=Invoke-Schemora $server ('/api/recipes/'+$id+'/rollback') POST @{version=1;expectedVersion=2}
    Assert-Schemora ($rolled.definition.version -eq 3 -and $rolled.validation.status -eq 'Broken') 'Rollback lost incompatible original';$checks++
    $restore=Invoke-Schemora $server ('/api/recipes/'+$id+'/rollback') POST @{version=2;expectedVersion=3}
    Assert-Schemora ($restore.definition.version -eq 4 -and $restore.validation.status -eq 'Valid') 'Restore repaired version failed';$checks++
    'CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY,NewCode bigint NULL);'|Set-Content -LiteralPath $sql
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='quick'};Wait-SchemoraJob $server $job.id|Out-Null
    $typeChanged=Invoke-Schemora $server ('/api/recipes/'+$id)
    Assert-Schemora ($typeChanged.validation.status -eq 'NeedsReview' -and @($typeChanged.validation.diagnostics|Where-Object code -eq 'DEPENDENCY_TYPE_CHANGED').Count -gt 0) 'Type change not diagnosed';$checks++
    [ordered]@{success=$true;checks=$checks;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/changes-report.json')
    Write-Host "PASS $checks schema diff/impact/repair HTTP checks"
}finally{Stop-SchemoraTestHost $server}

