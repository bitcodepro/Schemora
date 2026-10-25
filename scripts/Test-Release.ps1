[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') --nologo;if($LASTEXITCODE -ne 0){throw 'Build failed'}}
if(-not $Executable){$Executable=Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$run=Join-Path $root ('work/release-'+[guid]::NewGuid().ToString('N'));$fixture=Join-Path $run 'fixture';$data=Join-Path $run 'data'
New-Item -ItemType Directory -Path $fixture -Force|Out-Null
'CREATE TABLE dbo.Items(Id int NOT NULL PRIMARY KEY);'|Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql')
$server=$null;$checks=0
try{
    $server=Start-SchemoraTestHost $Executable $run $data
    $workspace=Invoke-Schemora $server '/api/workspaces' POST @{name='Release acceptance';roots=@($fixture);excludes=@()}
    $base='/api/workspaces/'+$workspace.id
    $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
    $catalog=Invoke-Schemora $server ($base+'/catalog');$db=$catalog.databases[0]
    $definition=@{id='x';name='Portable items';description='Generated';tags=@();dialect='sqlserver';databaseIds=@($db.id);version=0;sqlTemplate='SELECT i.Id FROM dbo.Items i WHERE i.Id IN ({{ids:Ids}});';inputs=@(@{name='Ids';type='int'});compatibilityLevel=130}
    Invoke-Schemora $server ($base+'/recipes') POST $definition|Out-Null
    $manifest=Invoke-Schemora $server ($base+'/configuration')
    Assert-Schemora ($manifest.formatVersion -eq 1 -and $manifest.recipes.Count -eq 1 -and -not ($manifest|ConvertTo-Json -Depth 40).Contains($fixture)) 'Manifest not portable';$checks++
    $mapping=@{};$mapping[$db.id]=$db.id
    $import=Invoke-Schemora $server ($base+'/configuration/import') POST @{manifest=$manifest;databaseMappings=$mapping}
    Assert-Schemora ($import.results.Count -eq 1 -and $import.results[0].status -eq 'Valid') 'Explicit portable import failed';$checks++
    try{Invoke-Schemora $server ($base+'/configuration/import') POST @{manifest=$manifest;databaseMappings=@{}}|Out-Null;throw 'Mapping not required'}catch{if($_.ErrorDetails.Message -notmatch 'CONFIGURATION_MAPPING_REQUIRED'){throw}};$checks++
    $storage=Invoke-Schemora $server '/api/storage'
    Assert-Schemora ($storage.catalogVersion -eq 2 -and $storage.activeJobs -eq 0) 'Storage diagnostics missing';$checks++
    $backup=Join-Path $run 'download.sqlite'
    Invoke-WebRequest -Uri ($server.Origin+'/api/storage/backup') -Method POST -Headers $server.Headers -OutFile $backup|Out-Null
    Assert-Schemora ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($backup),0,15) -eq 'SQLite format 3') 'Backup not valid SQLite file';$checks++
    $schedule=Invoke-Schemora $server '/api/storage/backup-schedule'
    Assert-Schemora ($schedule.schedule.enabled -and $schedule.schedule.intervalHours -eq 24 -and $schedule.schedule.keep -eq 7) 'Automatic backups are not on by default';$checks++
    Invoke-Schemora $server '/api/storage/backup-schedule' PUT @{enabled=$true;intervalHours=12;keep=1}|Out-Null
    Invoke-Schemora $server '/api/storage/backup-schedule/run' POST|Out-Null
    $schedule=Invoke-Schemora $server '/api/storage/backup-schedule/run' POST
    $auto=@(Get-ChildItem -LiteralPath (Join-Path $data 'backups') -Filter 'auto-*.sqlite')
    Assert-Schemora ($schedule.backups.Count -eq 1 -and $auto.Count -eq 1 -and $auto[0].Name -eq $schedule.backups[0].name) 'Automatic backups keep only the configured number of copies';$checks++
    Assert-Schemora (@(Get-ChildItem -LiteralPath (Join-Path $data 'backups') -Filter 'schemora-*.sqlite').Count -eq 1) 'Retention removed a manual backup';$checks++
    try{Invoke-Schemora $server '/api/storage/backup-schedule' PUT @{enabled=$true;intervalHours=0;keep=1}|Out-Null;throw 'Invalid schedule accepted'}catch{if($_.ErrorDetails.Message -notmatch 'BACKUP_SCHEDULE_INVALID'){throw}};$checks++
    $second=[Diagnostics.Process]::new();$second.StartInfo=[Diagnostics.ProcessStartInfo]::new($Executable);$second.StartInfo.UseShellExecute=$false;$second.StartInfo.CreateNoWindow=$true;$second.StartInfo.RedirectStandardOutput=$true;$second.StartInfo.RedirectStandardError=$true
    $secondSession=Join-Path $run 'second-session.json'
    foreach($arg in @('--no-browser','--data-dir',$data,'--session-file',$secondSession)){$second.StartInfo.ArgumentList.Add($arg)}
    [void]$second.Start();Assert-Schemora ($second.WaitForExit(10000)) 'Second instance did not return'
    Assert-Schemora ($second.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $secondSession)) 'Second host started for same catalog';$second.Dispose();$checks++
    Assert-Schemora ((Invoke-Schemora $server '/api/health').status -eq 'ok') 'Existing instance disrupted';$checks++
    Invoke-Schemora $server '/api/settings/theme' PUT @{value='light'}|Out-Null
    Invoke-Schemora $server '/api/application/shutdown' POST @{}|Out-Null
    Assert-Schemora ($server.Process.WaitForExit(15000)) 'Graceful shutdown timed out'
    Assert-Schemora (-not (Test-Path -LiteralPath $server.SessionPath)) 'Session token file left by graceful shutdown';$checks++
    Stop-SchemoraTestHost $server;$server=$null
    $server=Start-SchemoraTestHost $Executable $run $data @('--restore-backup',$backup)
    $recipes=@(Invoke-Schemora $server ($base+'/recipes'))
    Assert-Schemora ($recipes.Count -eq 2) 'Restored catalog missing recipes';$checks++
    $theme=Invoke-Schemora $server '/api/settings/theme'
    Assert-Schemora ($null -eq $theme.value) 'Restore did not recover backup state';$checks++
    Assert-Schemora (@(Get-ChildItem -LiteralPath $data -Filter 'before-restore-*.sqlite').Count -eq 1) 'Pre-restore backup missing';$checks++
    [ordered]@{success=$true;checks=$checks;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/release-report.json')
    Write-Host "PASS $checks release/recovery HTTP checks"
}finally{Stop-SchemoraTestHost $server}

