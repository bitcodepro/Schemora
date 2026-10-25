[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') --nologo -m:1;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/product-v13-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture';$data=Join-Path $run 'data'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
$sql=Join-Path $fixture 'Schema.sql'
'CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY);'|Set-Content -LiteralPath $sql
$server=$null;$checks=0
function Check([bool]$Condition,[string]$Message){Assert-Schemora $Condition $Message;$script:checks++}
function Refused([string]$Path,$Body,[string]$Code){
    try{Invoke-Schemora $server $Path POST $Body|Out-Null;throw 'Request unexpectedly succeeded'}catch{
        if($_.ErrorDetails.Message -notmatch [regex]::Escape($Code)){throw};$script:checks++
    }
}
try{
    $server=Start-SchemoraTestHost $Executable $run $data
    $ws=Invoke-Schemora $server '/api/workspaces' POST @{name='Artificial product v1.3 checks';roots=@($fixture);excludes=@()};$base='/api/workspaces/'+$ws.id
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
    $catalog=Invoke-Schemora $server ($base+'/catalog');$db=$catalog.databases[0]
    $definition=@{id='ignored';name='Artificial fixture consumer';description='Artificial values only';tags=@();dialect='sqlserver';databaseIds=@($db.id);version=0;sqlTemplate="SELECT d.DocumentId FROM dbo.Documents d WHERE d.DocumentId IN ({{ids:Ids}}); -- dbo.Documents stays";inputs=@(@{name='Ids';type='int';deduplicate=$false;preserveOrder=$true;strategy='auto'});compatibilityLevel=130}
    $recipe=Invoke-Schemora $server ($base+'/recipes') POST $definition;$id=$recipe.definition.id;$api='/api/recipes/'+$id
    $saveFixture=@{name='Artificial duplicates and order';expectedVersion=1;artificialDataConfirmed=$false;artificialInputs=@{Ids='2,1,2'};mode='standalone';strategy='auto';sqlContains=@('SELECT d.DocumentId');sqlExcludes=@('DELETE FROM')}
    Refused ($api+'/regression-fixtures') $saveFixture 'REGRESSION_ARTIFICIAL_REQUIRED'
    $saveFixture.artificialDataConfirmed=$true;$saveFixture.expectedVersion=99
    Refused ($api+'/regression-fixtures') $saveFixture 'RECIPE_VERSION_CONFLICT'
    $saveFixture.expectedVersion=1;$saved=Invoke-Schemora $server ($api+'/regression-fixtures') POST $saveFixture
    Check ($saved.expected.Ids.statistics.outputCount -eq 3 -and $saved.expected.Ids.statistics.duplicates -eq 1) 'Expected duplicate counts were not saved'
    Check (($saved.expected.Ids.values -join ',') -eq '2,1,2' -and ($saved.expected.Ids.ordinals -join ',') -eq '1,2,3') 'Expected order was not saved'
    Check ($saved.expectedSqlHash.Length -eq 64 -and $saved.expectedStrategy -eq 'values') 'SQL contract hash or selected strategy missing'
    $runs=@(Invoke-Schemora $server ($api+'/regression-runs'));Check ($runs.Count -eq 1 -and $runs[0].success) 'Baseline did not pass'
    $runtime='987654321'
    $render=Invoke-Schemora $server ($api+'/render') POST @{inputText=@{Ids=$runtime};mode='standalone';strategy='auto';acknowledgeReview=$false}
    Check ($render.sql.Contains($runtime)) 'Runtime render did not use request values'
    $fixtures=@(Invoke-Schemora $server ($api+'/regression-fixtures'));Check ($fixtures.Count -eq 1 -and $fixtures[0].artificialInputs.Ids -eq '2,1,2') 'Runtime values changed saved fixture'
    $recipe.definition.inputs[0].deduplicate=$true
    $recipe=Invoke-Schemora $server $api PUT $recipe.definition
    $runs=@(Invoke-Schemora $server ($api+'/regression-runs'));Check (-not $runs[0].success -and ($runs[0].failures -join ' ').Contains('duplicate')) 'Recipe save did not rerun and fail duplicate baseline'
    $recipe.definition.inputs[0].deduplicate=$false;$recipe.definition.sqlTemplate=$recipe.definition.sqlTemplate.Replace('SELECT d.DocumentId','SELECT d.DocumentId AS Changed')
    $recipe=Invoke-Schemora $server $api PUT $recipe.definition
    $runs=@(Invoke-Schemora $server ($api+'/regression-runs'));Check (-not $runs[0].success -and ($runs[0].failures -join ' ').Contains('SQL')) 'SQL change did not fail saved contract'
    $beforeCount=$runs.Count
    'CREATE TABLE sales.RenamedDocuments(DocumentId int NOT NULL PRIMARY KEY);'|Set-Content -LiteralPath $sql
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='quick'};Wait-SchemoraJob $server $job.id|Out-Null
    $runs=@(Invoke-Schemora $server ($api+'/regression-runs'));Check ($runs.Count -gt $beforeCount -and -not $runs[0].success) 'Schema check did not rerun saved fixtures'
    $graph=Invoke-Schemora $server ('/api/databases/'+$db.id+'/schema');$table=$graph.tables[0]
    $repair=@{databaseId=$db.id;tableId=$table.id;oldColumn='';newColumn='';kind='schema';oldSchema='dbo';oldTable='Documents';reason='Explicit artificial table and schema move';expectedVersion=3}
    $proposal=Invoke-Schemora $server ($api+'/repair-proposals') POST $repair
    Check ($proposal.afterSql.Contains('[sales].[RenamedDocuments]') -and $proposal.afterSql.Contains('-- dbo.Documents stays')) 'Table/schema repair preview damaged source comments'
    $still=Invoke-Schemora $server $api;Check ($still.definition.version -eq 3 -and $still.definition.sqlTemplate.Contains('FROM dbo.Documents')) 'Preview implicitly applied repair'
    # A new recipe version invalidates the old preview even when its SQL was unchanged.
    $still.definition.description='Artificial concurrent edit';$recipe=Invoke-Schemora $server $api PUT $still.definition
    Refused ('/api/repair-proposals/'+$proposal.id+'/approve') @{expectedVersion=3} 'RECIPE_VERSION_CONFLICT'
    $repair.expectedVersion=4;$proposal=Invoke-Schemora $server ($api+'/repair-proposals') POST $repair
    # A new schema snapshot invalidates preview context independently of recipe version.
    'CREATE TABLE sales.RenamedDocuments(DocumentId int NOT NULL PRIMARY KEY,Extra int NULL);'|Set-Content -LiteralPath $sql
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='quick'};Wait-SchemoraJob $server $job.id|Out-Null
    Refused ('/api/repair-proposals/'+$proposal.id+'/approve') @{expectedVersion=4} 'REPAIR_CONTEXT_CHANGED'
    $proposal=Invoke-Schemora $server ($api+'/repair-proposals') POST $repair
    $approved=Invoke-Schemora $server ('/api/repair-proposals/'+$proposal.id+'/approve') POST @{expectedVersion=4}
    Check ($approved.definition.version -eq 5 -and $approved.validation.status -in @('Valid','Warning')) 'Explicit approval did not create a usable new version'
    Refused ('/api/repair-proposals/'+$proposal.id+'/approve') @{expectedVersion=4} 'REPAIR_ALREADY_DECIDED'
    # Saved fixtures and results survive restart; inspect only this test's own stopped catalog.
    Stop-SchemoraTestHost $server;$server=$null
    foreach($file in Get-ChildItem -LiteralPath $data -Filter 'catalog.sqlite*'){
        $raw=[Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($file.FullName));Check (-not $raw.Contains($runtime)) 'Runtime ID was persisted into catalog'
    }
    $server=Start-SchemoraTestHost $Executable $run $data
    $fixtures=@(Invoke-Schemora $server ($api+'/regression-fixtures'));$runs=@(Invoke-Schemora $server ($api+'/regression-runs'))
    Check ($fixtures.Count -eq 1 -and $fixtures[0].expectedSqlHash -eq $saved.expectedSqlHash -and $runs.Count -gt 1) 'Regression fixtures or results did not survive restart'
    [ordered]@{success=$true;checks=$checks;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/product-v13-report.json')
    Write-Host "PASS $checks product v1.3 regression/rename HTTP checks"
}finally{Stop-SchemoraTestHost $server}
