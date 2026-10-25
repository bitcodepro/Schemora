[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){
    & dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -c Release --nologo
    if($LASTEXITCODE -ne 0){throw 'Build failed'}
}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Release/net10.0/Schemora.Host.exe'}
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$run=Join-Path $root ('work/enhancements-'+[guid]::NewGuid().ToString('N'))
$fixture=Join-Path $run 'synthetic-repository'
$hostOutput=Join-Path $run 'host-output'
New-Item -ItemType Directory -Path $fixture,$hostOutput -Force|Out-Null
if(Test-Path -LiteralPath ([IO.Path]::ChangeExtension($Executable,'.deps.json'))){
    Copy-Item -Path (Join-Path (Split-Path $Executable -Parent) '*') -Destination $hostOutput -Recurse
}else{Copy-Item -LiteralPath $Executable -Destination $hostOutput}
$taskExe=Join-Path $hostOutput (Split-Path $Executable -Leaf)
$sql=Join-Path $fixture 'Schema.sql'
$schema=@'
CREATE TABLE dbo.Alpha(Tenant int NOT NULL,Code int NOT NULL,Label nvarchar(50) NULL,CONSTRAINT PK_Alpha PRIMARY KEY(Tenant,Code));
GO
CREATE TABLE dbo.Beta(Tenant int NOT NULL,Code int NOT NULL,Label nvarchar(50) NULL,CONSTRAINT PK_Beta PRIMARY KEY(Tenant,Code));
GO
CREATE TABLE dbo.Gamma(Tenant int NOT NULL,Code int NOT NULL,Label nvarchar(50) NULL,CONSTRAINT PK_Gamma PRIMARY KEY(Tenant,Code),CONSTRAINT UQ_Gamma_Label UNIQUE(Label));
GO
CREATE INDEX IX_Gamma_Label ON dbo.Gamma(Label) INCLUDE(Code);
GO
CREATE VIEW dbo.GammaView AS SELECT Tenant,Code,Label FROM dbo.Gamma;
GO
CREATE PROCEDURE dbo.ReadGamma AS SELECT Tenant,Code FROM dbo.GammaView;
GO
'@
Set-Content -LiteralPath $sql -Value $schema
$server=$null;$checks=0
function Wait-AutoJob($Server,[string]$Base,[string]$Previous){
    $deadline=[datetime]::UtcNow.AddSeconds(50)
    do{
        $state=Invoke-Schemora $Server ($Base+'/auto-refresh')
        if($state.status.lastJobId -and $state.status.lastJobId -ne $Previous){
            Wait-SchemoraJob $Server $state.status.lastJobId|Out-Null
            return $state.status.lastJobId
        }
        if([datetime]::UtcNow -gt $deadline){throw 'File watcher did not queue an update'}
        Start-Sleep -Milliseconds 200
    }while($true)
}
try{
    $data=Join-Path $run 'data'
    $server=Start-SchemoraTestHost $taskExe $run $data
    $workspace=Invoke-Schemora $server '/api/workspaces' POST @{name='Synthetic enhancements pilot';roots=@($fixture);excludes=@()}
    $base='/api/workspaces/'+$workspace.id
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'}
    Wait-SchemoraJob $server $job.id|Out-Null
    $catalog=Invoke-Schemora $server ($base+'/catalog');$db=$catalog.databases[0]
    $tables=(Invoke-Schemora $server ($base+'/tables')).items
    $a=@($tables|Where-Object name -eq 'Alpha')[0];$b=@($tables|Where-Object name -eq 'Beta')[0];$c=@($tables|Where-Object name -eq 'Gamma')[0]
    Assert-Schemora ($a -and $b -and $c) 'Synthetic source tables were not discovered';$checks++
    $request=@{name='Alpha business key';fromTableId=$a.id;toTableId=$b.id;fromColumns=@('Tenant','Code');toColumns=@('Tenant','Code');reason='Synthetic documented composite key'}
    $manual=Invoke-Schemora $server ($base+'/manual-relations') POST $request
    Assert-Schemora ($manual.canUseForJoin -and $manual.version -eq 1) 'Manual composite relation was not trusted';$checks++
    $request.expectedVersion=1;$request.reason='Reviewed synthetic business contract'
    $manual=Invoke-Schemora $server ($base+'/manual-relations/'+$manual.id) PUT $request
    Assert-Schemora ($manual.version -eq 2) 'Manual edit did not increment its version';$checks++
    $conflict=Invoke-WebRequest ($server.Origin+$base+'/manual-relations/'+$manual.id) -Method Put -Headers $server.Headers -ContentType application/json -Body ($request|ConvertTo-Json -Depth 20) -SkipHttpErrorCheck
    Assert-Schemora ($conflict.StatusCode -eq 409) 'Stale manual edit overwrote the current mapping';$checks++
    $request=@{name='Beta to Gamma';fromTableId=$b.id;toTableId=$c.id;fromColumns=@('Tenant','Code');toColumns=@('Tenant','Code');reason='Synthetic cross-module contract'}
    $second=Invoke-Schemora $server ($base+'/manual-relations') POST $request
    $preview=Invoke-Schemora $server ($base+'/recipe-preview') POST @{fromTableId=$a.id;toTableId=$c.id;relationIds=@($manual.relationId,$second.relationId);inputColumn='Tenant';inputName='Ids';columns=@(@{tableId=$a.id;columnName='Code';alias='SourceCode'},@{tableId=$c.id;columnName='Label'});name='Synthetic chain recipe'}
    Assert-Schemora ($preview.definition.sqlTemplate.Contains('[t2].[Label]') -and $preview.definition.sqlTemplate.Contains('{{ids:Ids}}')) 'Chain preview lost chosen field/input';$checks++
    $recipe=Invoke-Schemora $server ($base+'/recipes') POST $preview.definition
    Assert-Schemora ($recipe.validation.status -in @('Valid','Warning')) 'Generated chain did not validate';$checks++
    $render=Invoke-Schemora $server ('/api/recipes/'+$recipe.definition.id+'/render') POST @{inputText=@{Ids='1,2,1'};mode='standalone';strategy='auto'}
    Assert-Schemora ($render.statistics.Ids.outputCount -eq 3) 'Chain input typing or duplicate-preserving default failed';$checks++
    $objects=Invoke-Schemora $server ($base+'/objects')
    Assert-Schemora (@($objects.items|Where-Object kind -eq 'view').Count -eq 1 -and @($objects.items|Where-Object kind -eq 'procedure').Count -eq 1) 'Extended object discovery failed';$checks++
    $detail=Invoke-Schemora $server ($base+'/objects/'+$c.id)
    Assert-Schemora ($detail.indexes.Count -ge 1 -and $detail.uniqueKeys.Count -eq 1 -and $detail.impact.objectIds.Count -eq 2 -and $detail.impact.recipeIds -contains $recipe.definition.id) 'Indexes, keys or transitive impact missing';$checks++
    $auto=Invoke-Schemora $server ($base+'/auto-refresh') PUT @{enabled=$true;debounceSeconds=2;reconcileMinutes=1}
    Assert-Schemora ($auto.settings.enabled -and $auto.status.watcherCount -eq 1) 'Watcher did not start';$checks++
    $last=Wait-AutoJob $server $base ''
    $schema=$schema.Replace('Label nvarchar(50)','RenamedLabel nvarchar(50)').Replace('UNIQUE(Label)','UNIQUE(RenamedLabel)').Replace('Gamma(Label)','Gamma(RenamedLabel)').Replace('Tenant,Code,Label FROM','Tenant,Code,RenamedLabel FROM')
    Set-Content -LiteralPath $sql -Value $schema
    $last=Wait-AutoJob $server $base $last
    $updated=Invoke-Schemora $server ('/api/recipes/'+$recipe.definition.id)
    Assert-Schemora ($updated.validation.status -eq 'Broken') 'Watcher scan did not revalidate dependent recipe after rename';$checks++
    $schema=$schema.Replace('dbo.Alpha(Tenant int','dbo.Alpha(Tenant bigint')
    Set-Content -LiteralPath $sql -Value $schema
    $last=Wait-AutoJob $server $base $last
    $saved=@(Invoke-Schemora $server ($base+'/manual-relations'))
    $stale=@($saved|Where-Object id -eq $manual.id)[0]
    Assert-Schemora ($stale.status -eq 'stale' -and -not $stale.canUseForJoin) 'Schema drift left manual mapping trusted';$checks++
    Stop-SchemoraTestHost $server;$server=$null
    $server=Start-SchemoraTestHost $taskExe $run $data
    $auto=Invoke-Schemora $server ($base+'/auto-refresh')
    Assert-Schemora ($auto.settings.enabled -and $auto.status.watcherCount -eq 1) 'Watcher settings did not survive restart';$checks++
    Wait-AutoJob $server $base ''|Out-Null
    $auto=Invoke-Schemora $server ($base+'/auto-refresh') PUT @{enabled=$false;debounceSeconds=2;reconcileMinutes=1}
    Assert-Schemora ($auto.status.state -eq 'disabled' -and $auto.status.watcherCount -eq 0) 'Disabling watcher did not dispose subscriptions';$checks++
    Invoke-Schemora $server ($base+'/manual-relations/'+$manual.id+'?expectedVersion=2') DELETE|Out-Null
    Assert-Schemora (@(Invoke-Schemora $server ($base+'/manual-relations')).Count -eq 1) 'Delete removed the wrong mapping';$checks++
    $allJobs=@(Invoke-Schemora $server '/api/jobs')
    Assert-Schemora (@($allJobs|Where-Object kind -eq 'scan:rebuild').Count -eq 0) 'Auto-refresh triggered a trusted project build';$checks++
    [ordered]@{success=$true;checks=$checks;kind='synthetic-repository';runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/enhancements-report.json')
    Write-Host "PASS $checks manual/chain/objects/watcher HTTP checks"
}finally{
    Stop-SchemoraTestHost $server
    $resolved=[IO.Path]::GetFullPath($hostOutput)
    if($resolved.StartsWith([IO.Path]::GetFullPath($run)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}


