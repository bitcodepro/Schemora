[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $Executable){$Executable=Join-Path $root 'artifacts/win-x64/Schemora.Host.exe'}
$run=Join-Path $root ('work/professional-'+[guid]::NewGuid().ToString('N'));$data=Join-Path $run 'data';New-Item -ItemType Directory -Path $run|Out-Null
$server=$null;$child=$null;$checks=0
function Check([bool]$Condition,[string]$Message){Assert-Schemora $Condition $Message;$script:checks++;Write-Output ('PASS '+$Message)}
function Refused([string]$Path,$Body,[string]$Code){try{Invoke-Schemora $server $Path POST $Body|Out-Null;throw 'Unexpected success'}catch{if($_.ErrorDetails.Message -notmatch [regex]::Escape($Code)){throw};$script:checks++}}
try{
 $server=Start-SchemoraTestHost $Executable $run $data
 $tutorial=Invoke-Schemora $server '/api/tutorial' POST;$base='/api/workspaces/'+$tutorial.workspace.id
 $source=Join-Path $tutorial.workspace.roots[0] 'Schema.sql'
 Add-Content -LiteralPath $source -Value "`nGO`nCREATE TABLE dbo.Staging(Value int NULL);`nGO`nCREATE TABLE dbo.[Клієнти]([Код] int NOT NULL PRIMARY KEY);"
 $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
 $findings=@(Invoke-Schemora $server ($base+'/quality'));$finding=$findings|Where-Object rule -eq 'TABLE_WITHOUT_PRIMARY_KEY'|Select-Object -First 1
 Check ($null -ne $finding -and $finding.severity -eq 'warning') 'Quality distinguishes an advisory missing key'
 Refused ($base+'/quality/review') @{findingId=$finding.id;fingerprint=$finding.fingerprint;reason='short'} 'QUALITY_REASON_REQUIRED'
 Invoke-Schemora $server ($base+'/quality/review') POST @{findingId=$finding.id;fingerprint=$finding.fingerprint;reason='Synthetic staging table deliberately has no primary key.'}|Out-Null
 $reviewed=@(Invoke-Schemora $server ($base+'/quality'))|Where-Object id -eq $finding.id
 Check ($null -ne $reviewed.suppressionReason) 'Quality review persists with rationale'
 $provenance=Invoke-Schemora $server ($base+'/provenance')
 Check (-not $provenance.stale -and $provenance.latest.sources.Count -gt 0 -and $provenance.latest.dacFxVersion) 'Analysis receipt records hashes and DacFx version'
 Add-Content -LiteralPath $source -Value "`n-- source fingerprint changed"
 Check ((Invoke-Schemora $server ($base+'/provenance')).stale) 'Freshness detects disk edits before scanning'
 $search=Invoke-Schemora $server ($base+'/search?query='+[uri]::EscapeDataString('код')+'&kind=column')
 Check ($search.total -eq 1 -and $search.items[0].name.Contains('Код')) 'Unified search supports Unicode column names'
 $search=Invoke-Schemora $server ($base+'/search?query=Orders&kind=recipe')
 Check ($search.total -eq 1 -and $search.items[0].id -eq $tutorial.recipeId) 'Unified search returns recipe navigation identity'
 $catalog=Invoke-Schemora $server ($base+'/catalog');$db=$catalog.databases[0];$graph=Invoke-Schemora $server ('/api/databases/'+$db.id+'/schema');$table=$graph.tables|Where-Object name -eq 'Orders'
 $cold=Invoke-Schemora $server ($base+'/dependency-paths?objectId='+[uri]::EscapeDataString($table.id));$warm=Invoke-Schemora $server ($base+'/dependency-paths?objectId='+[uri]::EscapeDataString($table.id))
 Check ($cold.refreshedDatabases -eq 1 -and $warm.refreshedDatabases -eq 0 -and $warm.total -eq 1) 'Unchanged dependency queries reuse the persisted index'
 $collection=Invoke-Schemora $server ($base+'/recipe-collections') PUT @{id='';name='Synthetic collection';recipeIds=@($tutorial.recipeId);version=0}
 Check ($collection.version -eq 1 -and (Invoke-Schemora $server ($base+'/recipe-collections')).collections.Count -eq 1) 'Recipe collection saves workspace membership'
 $render=$base+'/recipe-collections/'+$collection.id+'/render'
 $rendered=Invoke-Schemora $server $render POST @{inputText=@{Ids='7,8'};acknowledgeReview=$true}
 Check ($rendered.sql.Contains('-- 1/1: Orders for customers (v1)') -and $rendered.sql.Contains("`nGO`n") -and $null -eq $rendered.recipes[0].code) 'A collection renders into one script with a batch per recipe'
 Refused $render @{inputText=@{Other='1'}} 'INPUT_UNKNOWN'
 $before=Invoke-Schemora $server ($base+'/recipe-batch') POST @{recipeIds=@($tutorial.recipeId)}
 Check ($before.current.checks.Count -eq 1) 'Selected recipes receive actual validation results'
 (Get-Content -LiteralPath $source -Raw).Replace('OrderId','OrderKey')|Set-Content -LiteralPath $source
 $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
 $after=Invoke-Schemora $server ($base+'/recipe-batch') POST @{recipeIds=@($tutorial.recipeId)}
 Check ($after.changes[0].after -eq 'Broken' -and $after.changes[0].before -ne 'Broken' -and $after.previous.snapshots.($db.id) -ne $after.current.snapshots.($db.id)) 'Batch comparison detects a recipe broken by a new schema snapshot'
 Check ((Invoke-Schemora $server ($base+'/provenance')).recordedChangedPaths.Count -gt 0) 'Recorded runs explain changed source paths'
 Check ((Invoke-Schemora $server ($base+'/recipe-batch')).current.id -eq $after.current.id) 'Batch results remain available after leaving the screen'
 $broken=Invoke-Schemora $server $render POST @{inputText=@{Ids='7'};acknowledgeReview=$true}
 Check ($null -eq $broken.sql -and $broken.recipes[0].code -eq 'RECIPE_BROKEN') 'A collection with a broken recipe yields no partial script'
 Invoke-Schemora $server ($base+'/recipe-collections/'+$collection.id+'?expectedVersion=1') DELETE|Out-Null
 Check ((Invoke-Schemora $server ($base+'/recipe-collections')).collections.Count -eq 0 -and (Invoke-Schemora $server ('/api/recipes/'+$tutorial.recipeId)).definition.id -eq $tutorial.recipeId) 'Deleting a collection preserves its recipes'
 Check ((Invoke-Schemora $server '/api/storage/integrity').healthy) 'Catalog integrity passes'
 $backup=Join-Path $run 'verified.sqlite'
 Invoke-WebRequest -Uri ($server.Origin+'/api/storage/backup') -Method POST -Headers $server.Headers -OutFile $backup|Out-Null
 Invoke-Schemora $server '/api/workspaces' POST @{name='After backup';roots=@($tutorial.workspace.roots[0]);excludes=@()}|Out-Null
 $preview=Invoke-Schemora $server '/api/storage/recovery/preview' POST @{path=$backup}
 Check ($preview.workspaces -eq 1 -and (Invoke-Schemora $server '/api/workspaces').Count -eq 2) 'Restore preview preserves the live catalog'
 Refused '/api/storage/recovery/apply' @{previewId=$preview.id;confirmed=$false;noBrowser=$true} 'RECOVERY_CONFIRMATION_REQUIRED'
 Invoke-Schemora $server '/api/settings/theme' PUT @{value='light'}|Out-Null
 Refused '/api/storage/recovery/apply' @{previewId=$preview.id;confirmed=$true;noBrowser=$true} 'RECOVERY_CONTEXT_CHANGED'
 $preview=Invoke-Schemora $server '/api/storage/recovery/preview' POST @{path=$backup}
 Add-Content -LiteralPath $preview.stagedPath -Value 'Synthetic staged-copy tampering test'
 Refused '/api/storage/recovery/apply' @{previewId=$preview.id;confirmed=$true;noBrowser=$true} 'RECOVERY_BACKUP_CHANGED'
 $preview=Invoke-Schemora $server '/api/storage/recovery/preview' POST @{path=$backup}
 Invoke-Schemora $server '/api/storage/recovery/apply' POST @{previewId=$preview.id;confirmed=$true;noBrowser=$true}|Out-Null
 $deadline=[datetime]::UtcNow.AddSeconds(50);$runningPath=Join-Path $data 'runtime/running.json'
 do{Start-Sleep -Milliseconds 200;if(Test-Path -LiteralPath $runningPath){$running=Get-Content -LiteralPath $runningPath -Raw|ConvertFrom-Json;if($running.processId -ne $server.Process.Id){$child=Get-Process -Id $running.processId -ErrorAction SilentlyContinue;break}}}while([datetime]::UtcNow -lt $deadline)
 Check ($null -ne $child -and $server.Process.HasExited) 'Restore restarts Schemora after releasing the old instance lock'
 $restarted=[pscustomobject]@{Origin=$running.origin;Headers=@{'X-Schemora-Token'=$running.token;Origin=$running.origin}}
 Check (@(Invoke-Schemora $restarted '/api/workspaces').Count -eq 1) 'Restarted application uses the selected backup'
 $previous=Get-ChildItem -LiteralPath $data -Filter 'before-restore-*.sqlite'|Select-Object -First 1
 Check ($null -ne $previous -and (Invoke-Schemora $restarted '/api/storage/recovery/backups').Count -gt 0) 'The previous catalog is preserved and offered for rollback'
 $rollback=Invoke-Schemora $restarted '/api/storage/recovery/preview' POST @{path=$previous.FullName}
 $firstRestoredPid=$child.Id
 Invoke-Schemora $restarted '/api/storage/recovery/apply' POST @{previewId=$rollback.id;confirmed=$true;noBrowser=$true}|Out-Null
 $deadline=[datetime]::UtcNow.AddSeconds(50);$nextChild=$null
 do{Start-Sleep -Milliseconds 200;if(Test-Path -LiteralPath $runningPath){$running=Get-Content -LiteralPath $runningPath -Raw|ConvertFrom-Json;if($running.processId -ne $firstRestoredPid){$nextChild=Get-Process -Id $running.processId -ErrorAction SilentlyContinue;break}}}while([datetime]::UtcNow -lt $deadline)
 Check ($null -ne $nextChild -and $child.HasExited) 'Rollback performs a second verified application restart'
 $child.Dispose();$child=$nextChild
 $rolled=[pscustomobject]@{Origin=$running.origin;Headers=@{'X-Schemora-Token'=$running.token;Origin=$running.origin}}
 Check (@(Invoke-Schemora $rolled '/api/workspaces').Count -eq 2) 'Rollback restores the catalog preserved before the first restore'
 [ordered]@{success=$true;checks=$checks;restoredAfterRealRestart=$true;rollbackCopyPreserved=$true;rollbackExecuted=$true;runDirectory=$run}|ConvertTo-Json|Set-Content (Join-Path $root 'work/professional-report.json')
}finally{if($child -and -not $child.HasExited){$child.Kill($true);$child.WaitForExit();$child.Dispose()};Stop-SchemoraTestHost $server}
