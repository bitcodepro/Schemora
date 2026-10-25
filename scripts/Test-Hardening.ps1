[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $Executable){$Executable=Join-Path $root 'artifacts/win-x64/Schemora.Host.exe'}
$run=Join-Path $root ('work/hardening-'+[guid]::NewGuid().ToString('N'));$data=Join-Path $run 'data';New-Item -ItemType Directory -Path $run|Out-Null
$server=$null;$checks=0
function Check([bool]$Condition,[string]$Message){Assert-Schemora $Condition $Message;$script:checks++;Write-Output ('PASS '+$Message)}
function Refused([string]$Path,[string]$Method,$Body,[string]$Code){try{Invoke-Schemora $server $Path $Method $Body|Out-Null;throw 'Unexpected success'}catch{if($_.ErrorDetails.Message -notmatch [regex]::Escape($Code)){throw};$script:checks++}}
try{
 $server=Start-SchemoraTestHost $Executable $run $data
 $tutorial=Invoke-Schemora $server '/api/tutorial' POST;$base='/api/workspaces/'+$tutorial.workspace.id
 $source=Join-Path $tutorial.workspace.roots[0] 'Schema.sql'
 Add-Content -LiteralPath $source -Value @'

GO
CREATE TABLE dbo.OptionalChild(Id int NOT NULL PRIMARY KEY,CustomerId int NULL,CONSTRAINT FK_Optional FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId));
GO
CREATE INDEX IX_A ON dbo.OptionalChild(CustomerId);
GO
CREATE INDEX IX_B ON dbo.OptionalChild(CustomerId);
GO
CREATE PROCEDURE dbo.SearchCode AS
SELECT N'SearchMarkerКиїв' AS City;
GO
'@
 $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='full'};Wait-SchemoraJob $server $job.id|Out-Null
 $findings=@(Invoke-Schemora $server ($base+'/quality'))
 Check (@($findings|Where-Object rule -eq 'NULLABLE_FOREIGN_KEY').Count -gt 0) 'Nullable FK is reported as advisory evidence'
 Check (@($findings|Where-Object rule -eq 'DUPLICATE_INDEX').Count -eq 1) 'Matching recorded index definitions are detected'
 Check (@($findings|Where-Object {$_.rule -eq 'FK_WITHOUT_SUPPORTING_INDEX' -and $_.title -eq 'FK_Optional'}).Count -eq 0) 'Leading index keys cover the foreign key'
 $rules=Invoke-Schemora $server ($base+'/quality/rules')
 Check ($rules.definitions.Count -eq 8 -and $rules.policy.version -eq 0) 'Workspace rule definitions expose default severities'
 $saved=Invoke-Schemora $server ($base+'/quality/rules') PUT @{rules=@{NULLABLE_FOREIGN_KEY='off';DUPLICATE_INDEX='error'};version=0}
 $changed=@(Invoke-Schemora $server ($base+'/quality'))
 Check ($saved.version -eq 1 -and @($changed|Where-Object rule -eq 'NULLABLE_FOREIGN_KEY').Count -eq 0 -and ($changed|Where-Object rule -eq 'DUPLICATE_INDEX').severity -eq 'error') 'Workspace severity and disabling persist'
 Refused ($base+'/quality/rules') PUT @{rules=@{};version=0} 'QUALITY_POLICY_CHANGED'
 Refused ($base+'/quality/rules') PUT @{rules=@{Unknown='off'};version=1} 'QUALITY_POLICY_INVALID'
 $search=Invoke-Schemora $server ($base+'/search?kind=sqlCode&query='+[uri]::EscapeDataString('searchmarkerкиїв'))
 Check ($search.total -eq 1 -and $search.items[0].name -eq 'dbo.SearchCode' -and $search.items[0].detail.Length -le 300) 'SQL code search returns Unicode matches and bounded excerpts'
 $lines=Get-Content -LiteralPath $source;$expected=1;for($i=0;$i -lt $lines.Count;$i++){if($lines[$i].Contains('SearchMarker')){$expected=$i+1;break}}
 Check ($search.items[0].line -eq $expected -and $search.items[0].definitionLine -eq 2) 'SQL search preserves source and definition line coordinates'
 Check ((Invoke-Schemora $server ($base+'/search?kind=sqlCode&query=%25')).total -eq 0) 'SQL code search treats wildcard syntax literally'
 Check (@(Invoke-Schemora $server '/api/diagnostics/docker').Count -eq 0) 'Docker diagnostics start empty without fabricated failure history'
 $backups=Join-Path $data 'backups';New-Item -ItemType Directory -Path $backups -Force|Out-Null
 for($i=0;$i -lt 4;$i++){$p=Join-Path $backups ('before-cleanup-'+[guid]::NewGuid().ToString('N')+'.sqlite');Set-Content -LiteralPath $p 'Synthetic cleanup file, never restored';[IO.File]::SetLastWriteTimeUtc($p,[datetime]::UtcNow.AddDays(-10-$i))}
 $unknown=Join-Path $backups 'user-backup.sqlite';Set-Content -LiteralPath $unknown 'Protected unowned file';[IO.File]::SetLastWriteTimeUtc($unknown,[datetime]::UtcNow.AddDays(-10))
 $initial=Invoke-Schemora $server '/api/storage/files';Check ($initial.reclaimableBytes -eq 0) 'Default retention keeps recent test backups'
 Invoke-Schemora $server '/api/storage/files/policy' PUT @{keepDays=1;keepBackups=2}|Out-Null
 Refused '/api/storage/files/cleanup' POST @{expectedRevision=$initial.revision} 'CLEANUP_CHANGED'
 $preview=Invoke-Schemora $server '/api/storage/files'
 Check (@($preview.files|Where-Object eligible).Count -eq 2 -and -not ($preview.files|Where-Object path -eq $unknown).eligible) 'Cleanup protects two recent owned backups and all unknown files'
 $deleted=Invoke-Schemora $server '/api/storage/files/cleanup' POST @{expectedRevision=$preview.revision}
 Check ($deleted.deleted -eq 2 -and $deleted.deletedBytes -eq $preview.reclaimableBytes -and (Test-Path -LiteralPath $unknown)) 'Confirmed preview deletes exactly eligible files and reports bytes'
 Refused '/api/storage/files/policy' PUT @{keepDays=0;keepBackups=0} 'FILE_POLICY_INVALID'
 $job=Invoke-Schemora $server ($base+'/scan') POST @{mode='rebuild';trustProjects=$true};Wait-SchemoraJob $server $job.id|Out-Null
 $provenance=Invoke-Schemora $server ($base+'/provenance');$build=$provenance.latest.builds[0]
 Check ($build.toolchain.kind -eq 'SDK-style' -and $build.toolchain.sdkVersion -and $build.toolchain.msBuildVersion -and $build.toolchain.executableSha256.Length -eq 64 -and $build.toolchain.sqlProjectSdk.Contains('2.3.0')) 'Atomic scan receipt contains the actual SDK and MSBuild toolchain'
 $manifest=Invoke-Schemora $server ('/api/build-manifests/'+$build.manifestId)
 Check ($manifest.toolchain.sdkVersion -eq $build.toolchain.sdkVersion -and $manifest.toolchain.msBuildSha256.Length -eq 64) 'Build manifest and scan receipt agree on tool identity'
 $files=Invoke-Schemora $server '/api/storage/files'
 Check (@($files.files|Where-Object {$_.path.EndsWith($build.manifestId+'.json') -and -not $_.eligible}).Count -eq 1) 'Build manifest referenced by the current snapshot is protected'
 [ordered]@{success=$true;checks=$checks;runDirectory=$run;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'work/hardening-report.json')
}finally{Stop-SchemoraTestHost $server}
