[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){ & dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -c Debug --nologo; if($LASTEXITCODE -ne 0){throw 'Build failed'} }
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/recipes-'+[guid]::NewGuid().ToString('N'))
$fixture=Join-Path $run 'fixture'; New-Item -ItemType Directory -Path $fixture -Force | Out-Null
'CREATE TABLE dbo.Invoices(InvoiceId int NOT NULL PRIMARY KEY,Code nvarchar(80) NULL);' | Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql')
$server=$null; $checks=0
function Expect-Code([scriptblock]$Action,[string]$Code){
    try { & $Action | Out-Null; throw "Expected $Code" } catch {
        if($_.ErrorDetails.Message -notmatch [regex]::Escape($Code)){throw}
    }
}
try {
    $server=Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
    $workspace=Invoke-Schemora $server '/api/workspaces' POST @{name='Recipes acceptance';roots=@($fixture);excludes=@()}
    $base='/api/workspaces/'+$workspace.id
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'}; Wait-SchemoraJob $server $job.id | Out-Null
    $catalog=Invoke-Schemora $server ($base+'/catalog'); $db=$catalog.databases[0]
    $definition=@{id='ignored';name='Invoices by IDs';description='Synthetic acceptance';tags=@('test');dialect='sqlserver';databaseIds=@($db.id);version=0;sqlTemplate='SELECT i.InvoiceId FROM dbo.Invoices AS i WHERE i.InvoiceId IN ({{ids:Ids}});';inputs=@(@{name='Ids';type='int';deduplicate=$false;preserveOrder=$true;strategy='auto'});compatibilityLevel=130}
    $entry=Invoke-Schemora $server ($base+'/recipes') POST $definition; $id=$entry.definition.id
    Assert-Schemora ($entry.definition.version -eq 1 -and $entry.validation.status -eq 'Valid') ('Validation: '+($entry.validation|ConvertTo-Json -Depth 8)); $checks++
    Assert-Schemora (@($entry.definition.dependencies|Where-Object columnName -eq 'InvoiceId').Count -gt 0) 'Dependencies not inferred';$checks++
    $ids=(1..10000)-join ','
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $parsed=Invoke-Schemora $server '/api/inputs/parse' POST @{text=$ids;input=$definition.inputs[0]}
    Assert-Schemora ($parsed.statistics.total -eq 10000 -and $parsed.statistics.valid -eq 10000 -and $parsed.statistics.outputCount -eq 10000 -and $parsed.values[9999].value -eq '10000') '10k parser lost values';$checks++
    $render=Invoke-Schemora $server ('/api/recipes/'+$id+'/render') POST @{inputText=@{Ids=$ids};mode='standalone';strategy='auto'}
    Assert-Schemora ($render.statistics.Ids.outputCount -eq 10000 -and $render.sql -match '10000' -and $render.strategy -eq 'temp') '10k scalable render failed';$checks++
    $elapsed=$watch.ElapsedMilliseconds
    $repeat=Invoke-Schemora $server ('/api/recipes/'+$id+'/render') POST @{inputText=@{Ids=$ids};mode='standalone';strategy='auto'}
    Assert-Schemora ($render.sql -ceq $repeat.sql) 'Nondeterministic render';$checks++
    $param=Invoke-Schemora $server ('/api/recipes/'+$id+'/render') POST @{inputText=@{Ids=$ids};mode='parameterized';strategy='auto'}
    Assert-Schemora ($param.parameters.Count -eq 1 -and $param.parameters[0].name -eq '@SchemoraInputs' -and $param.sql -match 'OPENJSON') 'One JSON parameter expected';$checks++
    $bad=Invoke-Schemora $server '/api/inputs/parse' POST @{text=('1,wrong,2'+[Environment]::NewLine+'2147483648');input=$definition.inputs[0]}
    Assert-Schemora ($bad.statistics.invalid -eq 2 -and $bad.errors[0].line -eq 1 -and $bad.errors[1].line -eq 2) 'Error positions missing';$checks++
    Expect-Code { Invoke-Schemora $server ('/api/recipes/'+$id+'/render') POST @{inputText=@{Ids='1,wrong,2'}} } 'ID_VALIDATION_FAILED';$checks++
    $dup=Invoke-Schemora $server '/api/inputs/parse' POST @{text='3,1,3,2';input=@{name='Ids';type='int';deduplicate=$true;preserveOrder=$true;strategy='auto'}}
    Assert-Schemora ($dup.statistics.duplicates -eq 1 -and ($dup.values.value -join ',') -eq '3,1,2') 'Dedup/order failed';$checks++
    $export=Invoke-Schemora $server ('/api/recipes/'+$id+'/export')
    Assert-Schemora ($export.formatVersion -eq 1 -and -not ($export|ConvertTo-Json -Depth 30).Contains($fixture)) 'Export contains source paths';$checks++
    $lineage=@(Invoke-Schemora $server ('/api/recipes/'+$id+'/lineage'))
    Assert-Schemora (@($lineage|Where-Object {$_.tableName -eq 'Invoices' -and $_.columnName -eq 'InvoiceId'}).Count -gt 0) 'Lineage missing';$checks++
    $definition.version=1; $definition.name='Edited recipe'
    $edited=Invoke-Schemora $server ('/api/recipes/'+$id) PUT $definition
    Assert-Schemora ($edited.definition.version -eq 2) 'Version did not advance';$checks++
    Expect-Code { Invoke-Schemora $server ('/api/recipes/'+$id) PUT $definition } 'RECIPE_VERSION_CONFLICT';$checks++
    $rolled=Invoke-Schemora $server ('/api/recipes/'+$id+'/rollback') POST @{version=1;expectedVersion=2}
    Assert-Schemora ($rolled.definition.version -eq 3 -and $rolled.definition.name -eq 'Invoices by IDs') 'Rollback must create new version';$checks++
    $versions=@(Invoke-Schemora $server ('/api/recipes/'+$id+'/versions'))
    Expect-Code { Invoke-Schemora $server ('/api/recipes/'+$id+'/render') POST @{inputText=@{Ids='1,2'};expectedVersion=1} } 'RECIPE_VERSION_STALE';$checks++
    $current=Invoke-Schemora $server ('/api/recipes/'+$id+'/render') POST @{inputText=@{Ids='1,2'};expectedVersion=3}
    Assert-Schemora ($current.recipeVersion -eq 3) 'Render does not report the version it used';$checks++
    Assert-Schemora ($versions.Count -eq 3 -and ($versions.version -join ',') -eq '3,2,1') 'History missing';$checks++
    $clone=Invoke-Schemora $server ('/api/recipes/'+$id+'/clone') POST @{name='Cloned recipe'}
    Assert-Schemora ($clone.definition.id -ne $id -and $clone.definition.version -eq 1) 'Clone failed';$checks++
    $synthetic=Invoke-Schemora $server ('/api/recipes/'+$id+'/tests') POST @{}
    Assert-Schemora $synthetic.success 'Synthetic test failed';$checks++
    $textDefinition=@{id='ignored';name='Text IDs';description='Escaping';tags=@();dialect='sqlserver';databaseIds=@($db.id);version=0;sqlTemplate='SELECT i.Code FROM dbo.Invoices i WHERE i.Code IN ({{ids:Codes}});';inputs=@(@{name='Codes';type='nvarchar';maxLength=120;deduplicate=$false;preserveOrder=$true;strategy='auto'});compatibilityLevel=130}
    $text=Invoke-Schemora $server ($base+'/recipes') POST $textDefinition
    $sentinel='SchemoraRuntimeSentinel_'+[guid]::NewGuid().ToString('N')
    $payload=$sentinel+"'; DROP TABLE dbo.Invoices;--"
    $quoted='"'+$payload.Replace('"','""')+'"'+[Environment]::NewLine+'"Юникод"'
    $escaped=Invoke-Schemora $server ('/api/recipes/'+$text.definition.id+'/render') POST @{inputText=@{Codes=$quoted};strategy='values';mode='standalone';acknowledgeReview=$true}
    Assert-Schemora ($escaped.statistics.Codes.outputCount -eq 2 -and $escaped.sql.Contains("''; DROP TABLE") -and $escaped.sql.Contains("N'Юникод'")) 'Unicode/escaping failed';$checks++
    $guid=Invoke-Schemora $server '/api/inputs/parse' POST @{text='00112233-4455-6677-8899-AABBCCDDEEFF';input=@{name='G';type='uniqueidentifier'}}
    Assert-Schemora ($guid.statistics.valid -eq 1 -and $guid.values[0].value -ceq '00112233-4455-6677-8899-AABBCCDDEEFF') 'GUID changed';$checks++
    $varchar=Invoke-Schemora $server '/api/inputs/parse' POST @{text='Юникод';input=@{name='V';type='varchar';maxLength=80}}
    Assert-Schemora ($varchar.statistics.invalid -eq 1) 'Lossy varchar accepted';$checks++
    # Saved value sets are the one place runtime values are kept: encrypted, so the scan below must still not find them.
    $valueSets='/api/recipes/'+$text.definition.id+'/value-sets'
    $set=Invoke-Schemora $server $valueSets PUT @{name='Acceptance set';values=@{Codes=$quoted}}
    Assert-Schemora (@(Invoke-Schemora $server $valueSets).name -contains 'Acceptance set') 'Saved values are not listed';$checks++
    Expect-Code { Invoke-Schemora $server $valueSets PUT @{name='Unknown input';values=@{Other='1'}} } 'VALUE_SET_INPUT_UNKNOWN';$checks++
    Stop-SchemoraTestHost $server; $server=$null
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $run 'data') -File -Recurse){
        $bytes=[IO.File]::ReadAllBytes($file.FullName)
        Assert-Schemora (-not ([Text.Encoding]::UTF8.GetString($bytes).Contains($sentinel) -or [Text.Encoding]::Unicode.GetString($bytes).Contains($sentinel))) ('Runtime values persisted in '+$file.Name)
    };$checks++
    Assert-Schemora (Test-Path -LiteralPath (Join-Path $run ('data/value-sets/'+$text.definition.id+'.dat'))) 'Encrypted value set file missing';$checks++
    $server=Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
    $persisted=Invoke-Schemora $server ('/api/recipes/'+$id)
    Assert-Schemora ($persisted.definition.version -eq 3 -and $persisted.validation.status -eq 'Valid') 'Recipe lost after restart';$checks++
    Assert-Schemora ((Invoke-Schemora $server ($valueSets+'/'+$set.id)).values.Codes -ceq $quoted) 'Saved values lost after restart';$checks++
    Invoke-Schemora $server ($valueSets+'/'+$set.id) DELETE | Out-Null
    Assert-Schemora (-not (Test-Path -LiteralPath (Join-Path $run ('data/value-sets/'+$text.definition.id+'.dat')))) 'Deleting the last value set leaves its file';$checks++
    [ordered]@{success=$true;checks=$checks;ids=10000;parseAndRenderMs=$elapsed;runtimeValuesPersisted=$false;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content (Join-Path $root 'work/recipes-report.json')
    Write-Host "PASS $checks recipe HTTP checks; 10000 IDs in $elapsed ms"
} finally {Stop-SchemoraTestHost $server}


