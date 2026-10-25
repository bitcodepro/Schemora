#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Run,[string]$Executable,[switch]$SkipBuild,[string]$DockerPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not $Run){throw 'Opt-in required: pwsh -File scripts/Test-DockerSchema.ps1 -Run. Uses only owned temporary Docker resources and synthetic DACPAC metadata.'}
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $repo 'src/Schemora.Host/Schemora.Host.csproj') -c Debug --nologo;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $repo 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
if(-not $DockerPath){$command=Get-Command docker -ErrorAction SilentlyContinue;if($command){$DockerPath=$command.Source}else{$DockerPath=Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/resources/bin/docker.exe'}}
$runDirectory=Join-Path $repo ('work/docker-schema-'+[guid]::NewGuid().ToString('N'))
$hostCopy=Join-Path $runDirectory 'host';$data=Join-Path $runDirectory 'data'
New-Item -ItemType Directory -Force -Path $hostCopy | Out-Null
# Use an independent artifact copy so a test process does not lock an ongoing build/publish output.
$sourceDirectory=Split-Path $Executable -Parent
if(Test-Path -LiteralPath (Join-Path $sourceDirectory 'Schemora.Host.dll')){Get-ChildItem -LiteralPath $sourceDirectory | Copy-Item -Destination $hostCopy -Recurse}
else{Copy-Item -LiteralPath $Executable -Destination $hostCopy}
$testExecutable=Join-Path $hostCopy (Split-Path $Executable -Leaf)
$fixture=Join-Path $repo 'tests/Fixtures/Commerce/bin/Debug/Commerce.dacpac'
if(-not (Test-Path -LiteralPath $fixture)){& dotnet build (Join-Path $repo 'tests/Fixtures/Commerce/Commerce.sqlproj') -c Debug --nologo;if($LASTEXITCODE -ne 0){throw 'Synthetic Commerce fixture build failed'}}
$metadataFixture=Join-Path $repo 'tests/Fixtures/Metadata/bin/Debug/Metadata.dacpac'
& dotnet build (Join-Path $repo 'tests/Fixtures/Metadata/Metadata.sqlproj') -c Debug --nologo
if($LASTEXITCODE -ne 0){throw 'Synthetic rich Metadata fixture build failed'}
$server=$null;$checks=0;$failure=$null;$priorPath=$env:PATH
$ownedSynthetic=@()
$report=[ordered]@{success=$false;checks=0;cleanedUp=$false;runDirectory=$runDirectory}
function Docker([string[]]$Arguments){$result=& $DockerPath @Arguments 2>&1;if($LASTEXITCODE -ne 0){throw ('Docker command failed: '+($result -join ' '))};return ($result -join "`n").Trim()}
function Inventory {return @(Docker @('ps','--all','--filter','label=com.schemora.purpose=schema-preview','--format','{{.ID}}') | Where-Object {$_}) -join '|'}
function Expect-Code([scriptblock]$Action,[string]$Code){try{&$Action|Out-Null;throw ('Expected '+$Code)}catch{if($_.ErrorDetails.Message -notmatch [regex]::Escape($Code)){throw}}}
try{
 $env:PATH=(Join-Path $repo 'work/tools')+[IO.Path]::PathSeparator+$priorPath
 $before=Inventory
 $server=Start-SchemoraTestHost $testExecutable $runDirectory $data
 Expect-Code {Invoke-Schemora $server '/api/schema/docker' POST @{path=$fixture;allowIsolatedDocker=$false}} 'DOCKER_OPT_IN_REQUIRED';$checks++
 foreach($payload in @('predeploy.sql','Data/dbo.Items.bcp')){
  $unsafe=Join-Path $runDirectory ('unsafe-'+[guid]::NewGuid().ToString('N')+'.dacpac')
  $zip=[IO.Compression.ZipFile]::Open($unsafe,[IO.Compression.ZipArchiveMode]::Create)
  try{foreach($entryName in @('model.xml','DacMetadata.xml',$payload)){$entry=$zip.CreateEntry($entryName);$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('<synthetic />');$writer.Dispose()}}finally{$zip.Dispose()}
  Expect-Code {Invoke-Schemora $server '/api/schema/docker' POST @{path=$unsafe;allowIsolatedDocker=$true}} 'DACPAC_SCHEMA_ONLY_REQUIRED';$checks++
 }
 Write-Host 'HTTP Docker schema verification: deploying and extracting synthetic Commerce metadata...'
 $graph=Invoke-RestMethod -Uri ($server.Origin+'/api/schema/docker') -Headers $server.Headers -Method POST -ContentType 'application/json' -Body (@{path=$fixture;allowIsolatedDocker=$true}|ConvertTo-Json -Compress) -TimeoutSec 420
 Assert-Schemora ($graph.tables.Count -eq 4 -and $graph.relations.Count -eq 1) 'Actual Docker schema graph has wrong counts';$checks++
 Assert-Schemora ((@($graph.tables | ForEach-Object {$_.schema+'.'+$_.name}) -join '|') -eq 'dbo.Customers|dbo.Documents|sales.Customers|sales.Orders') 'Qualified Docker table names mismatch';$checks++
 Assert-Schemora (($graph.relations[0].fromColumns -join '|') -eq 'TenantId|CustomerId' -and ($graph.relations[0].toColumns -join '|') -eq 'TenantId|CustomerId') 'Ordered composite Docker FK mismatch';$checks++
 Assert-Schemora ($graph.sourcePath -eq $fixture -and @($graph.diagnostics|Where-Object code -eq 'DOCKER_SCHEMA_VERIFIED').Count -eq 1) 'Verification source/diagnostic missing';$checks++
 Assert-Schemora ((Inventory) -eq $before) 'Container leaked after successful HTTP verification';$checks++
 Assert-Schemora (@(Get-ChildItem -LiteralPath (Join-Path $data 'docker-runs') -Directory).Count -eq 0) 'Credential/stage directory leaked';$checks++
 Write-Host 'HTTP Docker rich metadata: views, procedures, functions, synonym, unique/index and dependency inventory...'
 $rich=Invoke-RestMethod -Uri ($server.Origin+'/api/schema/docker') -Headers $server.Headers -Method POST -ContentType 'application/json' -Body (@{path=$metadataFixture;allowIsolatedDocker=$true}|ConvertTo-Json -Compress) -TimeoutSec 420
 Assert-Schemora ($rich.tables.Count -eq 2 -and $rich.objects.Count -eq 8 -and $rich.indexes.Count -eq 6) 'Rich Docker catalog object/index counts mismatch';$checks++
 $overview=$rich.objects|Where-Object name -eq 'ItemOverview'
 Assert-Schemora (($overview.columns.dataType -join '|') -eq 'int|varchar(24)|nvarchar(80)') 'Actual view output data types mismatch';$checks++
 $scalar=$rich.objects|Where-Object name -eq 'FormatCode'
 Assert-Schemora ($scalar.returnType -eq 'nvarchar(80)' -and ($scalar.parameters.dataType -join '|') -eq 'varchar(24)|decimal(9,2)') 'Scalar function signature mismatch';$checks++
 $inline=$rich.objects|Where-Object name -eq 'ItemsForCategory'
 Assert-Schemora (($inline.columns.dataType -join '|') -eq 'int|varchar(24)|int') 'Actual inline function columns mismatch';$checks++
 $multi=$rich.objects|Where-Object name -eq 'ItemTokens'
 Assert-Schemora ($multi.columns[0].dataType -eq 'nvarchar(40)' -and -not $multi.columns[0].isNullable) 'Multi-statement function column contract mismatch';$checks++
 $procedure=$rich.objects|Where-Object name -eq 'GetItems'
 Assert-Schemora ($procedure.parameters.Count -eq 3 -and ($procedure.parameters|Where-Object name -eq '@total').isOutput -and $procedure.definition -match 'CREATE PROCEDURE') 'Procedure metadata/source missing';$checks++
 $items=$rich.tables|Where-Object name -eq 'Items'
 Assert-Schemora ($items.uniqueKeys.Count -eq 1 -and $items.uniqueKeys[0].name -eq 'UQ_Items_Code' -and ($items.uniqueKeys[0].columns -join '|') -eq 'Code') 'Unique constraint metadata mismatch';$checks++
 $index=$rich.indexes|Where-Object name -eq 'IX_Items_Category'
 Assert-Schemora (($index.columns.name -join '|') -eq 'CategoryId|Code' -and $index.columns[0].descending -and -not $index.columns[1].descending -and ($index.includedColumns -join '|') -eq 'Payload' -and $index.filter -match 'IS NOT NULL') 'Index order/include/filter mismatch';$checks++
 Assert-Schemora (($rich.indexes|Where-Object name -eq 'CIX_ItemKeys').objectId -eq ($rich.objects|Where-Object name -eq 'ItemKeys').id) 'Indexed view owner mismatch';$checks++
 Assert-Schemora ($rich.objectDependencies.Count -ge 9 -and @($rich.objectDependencies|Where-Object { -not $_.toObjectId }).Count -eq 0 -and @($rich.objectDependencies|Where-Object kind -eq 'synonym-target').Count -eq 1) 'Actual object dependency evidence missing/unresolved';$checks++
 Assert-Schemora (@($rich.objects|Where-Object { -not $_.definition -or $_.isPartial }).Count -eq 0) 'Catalog definitions were unexpectedly missing or partial';$checks++
 Assert-Schemora ((Inventory) -eq $before -and @(Get-ChildItem -LiteralPath (Join-Path $data 'docker-runs') -Directory).Count -eq 0) 'Rich Docker resources leaked';$checks++
 $report.richMetadata=@{tables=2;objects=8;indexes=6;dependencies=$rich.objectDependencies.Count;exactTypes=$true}
 Stop-SchemoraTestHost $server;$server=$null
 # Simulate a process crash using stopped owned resources and a matching durable marker, without credentials.
 $owner=[guid]::NewGuid().ToString('N');$name='schemora-schema-'+$owner;$networkName='schemora-net-'+$owner
 $networkId=Docker @('network','create','--internal','--label',"com.schemora.owner=$owner",$networkName)
 $containerId=Docker @('create','--name',$name,'--network',$networkName,'--label',"com.schemora.owner=$owner",'--label','com.schemora.purpose=schema-preview','mcr.microsoft.com/mssql/server:2022-latest')
 $ownedSynthetic+=@{owner=$owner;containerId=$containerId;networkId=$networkId}
 $markerDirectory=Join-Path (Join-Path $data 'docker-runs') $owner;New-Item -ItemType Directory -Path $markerDirectory | Out-Null
 @{Owner=$owner;ContainerName=$name;NetworkName=$networkName}|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $markerDirectory 'owner.json')
 $server=Start-SchemoraTestHost $testExecutable $runDirectory $data
 Assert-Schemora ((Inventory) -eq $before) 'Startup failed to recover its own stopped container';$checks++
 Assert-Schemora (-not (Test-Path -LiteralPath $markerDirectory)) 'Recovered owner marker was not removed';$checks++
 $status=Invoke-Schemora $server '/api/schema/docker/status'
 Assert-Schemora ($status.recoveryDiagnostics.Count -eq 0) 'Startup recovery reported pending work';$checks++
 $report.success=$true;$report.cleanedUp=$true;$report.tables=4;$report.orderedForeignKeyColumns=2;$report.startupRecovery=$true
}catch{$failure=$_.Exception.Message+' '+$_.ErrorDetails.Message;$report.error=$failure}
finally{
 Stop-SchemoraTestHost $server
 $env:PATH=$priorPath
 foreach($owned in $ownedSynthetic){
  $containers=Docker @('ps','--all','--no-trunc','--filter',('label=com.schemora.owner='+$owned.owner),'--format','{{.ID}}')
  if($containers){if($containers -ne $owned.containerId){throw 'Synthetic cleanup ownership mismatch'};[void](Docker @('rm','--force','--volumes',$owned.containerId))}
  $networks=Docker @('network','ls','--no-trunc','--filter',('label=com.schemora.owner='+$owned.owner),'--format','{{.ID}}')
  if($networks){if($networks -ne $owned.networkId){throw 'Synthetic network cleanup ownership mismatch'};[void](Docker @('network','rm',$owned.networkId))}
 }
 $report.checks=$checks;$report.at=[datetime]::UtcNow.ToString('O')
 $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $repo 'work/docker-schema-report.json')
}
if($failure){throw $failure}
Write-Host ('PASS '+$checks+' Docker schema HTTP/recovery checks. Own resources removed.')
