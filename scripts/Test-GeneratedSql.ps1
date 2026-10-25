#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Run,[string]$DockerPath,[ValidateRange(30,600)][int]$StartupTimeoutSeconds=180)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not $Run){throw 'Opt-in required: pwsh -File scripts/Test-GeneratedSql.ps1 -Run. Runs synthetic SQL only in an owned temporary container.'}
$repo=Split-Path $PSScriptRoot -Parent
$work=Join-Path $repo 'work'
$owner=[guid]::NewGuid().ToString('N')
$containerName='schemora-generated-'+$owner
$containerId=$null
$attempted=$false
$secret='ScH9!'+[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$failure=$null
$report=[ordered]@{status='Running';startedAtUtc=[datetime]::UtcNow.ToString('O');container=$containerName;cleanedUp=$false;generatedSqlPersisted=$false}
function Invoke-Tool {
 param([string]$File,[string[]]$Arguments,[hashtable]$Environment=@{},[int]$TimeoutSeconds=120,[switch]$AllowFailure)
 $start=[Diagnostics.ProcessStartInfo]::new()
 $start.FileName=$File;$start.WorkingDirectory=$repo;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($argument in $Arguments){[void]$start.ArgumentList.Add($argument)}
 foreach($entry in $Environment.GetEnumerator()){$start.Environment[$entry.Key]=[string]$entry.Value}
 $process=[Diagnostics.Process]::new();$process.StartInfo=$start
 try{
  [void]$process.Start()
  $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  $completion=[Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($process.WaitForExitAsync(),$stdout,$stderr))
  if(-not $completion.Wait([timespan]::FromSeconds($TimeoutSeconds))){try{$process.Kill($true)}catch{};throw ('Tool timeout: '+[IO.Path]::GetFileName($File))}
  $result=[pscustomobject]@{ExitCode=$process.ExitCode;Output=$stdout.GetAwaiter().GetResult().Replace($secret,'[REDACTED]');Error=$stderr.GetAwaiter().GetResult().Replace($secret,'[REDACTED]')}
  if($result.ExitCode -ne 0 -and -not $AllowFailure){throw ([IO.Path]::GetFileName($File)+' failed: '+$result.Output+' '+$result.Error)}
  return $result
 }finally{$process.Dispose()}
}
try{
 New-Item -ItemType Directory -Force -Path $work | Out-Null
 if(-not $DockerPath){$found=Get-Command docker -ErrorAction SilentlyContinue;if($found){$DockerPath=$found.Source}else{$DockerPath=Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/resources/bin/docker.exe'}}
 if(-not(Test-Path -LiteralPath $DockerPath -PathType Leaf)){throw 'Docker CLI unavailable; pass -DockerPath.'}
 [void](Invoke-Tool $DockerPath @('version','--format','{{.Server.Version}}'))
 Write-Host 'Building synthetic generated-SQL harness...'
 [void](Invoke-Tool 'dotnet' @('build','tests/GeneratedSqlHarness/GeneratedSqlHarness.csproj','-c','Debug','--nologo') -TimeoutSeconds 300)
 Write-Host 'Starting owned isolated SQL Server container on an ephemeral loopback port...'
 $attempted=$true
 $created=Invoke-Tool $DockerPath @('run','--detach','--name',$containerName,'--label',"com.schemora.owner=$owner",'--label','com.schemora.purpose=generated-sql-smoke','--publish','127.0.0.1::1433','--memory','3g','--cpus','2','--env','ACCEPT_EULA=Y','--env','MSSQL_PID=Developer','--env','MSSQL_SA_PASSWORD','mcr.microsoft.com/mssql/server:2022-latest') -Environment @{MSSQL_SA_PASSWORD=$secret} -TimeoutSeconds 600
 $containerId=$created.Output.Trim()
 if($containerId -notmatch '^[0-9a-f]{64}$'){throw 'Unexpected container identity.'}
 $inspection=(Invoke-Tool $DockerPath @('inspect',$containerId)).Output | ConvertFrom-Json
 $ports=@($inspection[0].NetworkSettings.Ports.'1433/tcp')
 if($ports.Count -ne 1 -or $ports[0].HostIp -ne '127.0.0.1'){throw 'SQL port was not bound exclusively to loopback.'}
 $port=[int]$ports[0].HostPort
 $report.loopbackOnly=$true
 $ready=$false;$deadline=[datetime]::UtcNow.AddSeconds($StartupTimeoutSeconds);$lastProgress=[datetime]::MinValue
 do{
  $check=Invoke-Tool $DockerPath @('exec','--env','SQLCMDPASSWORD',$containerId,'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-l','3','-Q','SET NOCOUNT ON; SELECT 1;') -Environment @{SQLCMDPASSWORD=$secret} -TimeoutSeconds 15 -AllowFailure
  if($check.ExitCode -eq 0){$ready=$true;break}
  if(($check.Output+$check.Error) -match 'no such file or directory|executable file not found'){throw 'SQL Server image is missing sqlcmd.'}
  if(([datetime]::UtcNow-$lastProgress).TotalSeconds -ge 15){Write-Host 'Waiting for temporary SQL Server readiness...';$lastProgress=[datetime]::UtcNow}
  Start-Sleep -Seconds 2
 }while([datetime]::UtcNow -lt $deadline)
 if(-not $ready){throw 'Temporary SQL Server did not become ready.'}
 Write-Host 'Executing renderer-produced SQL and verifying exact rows, ordinals, Unicode and code pages...'
 $harness=Invoke-Tool 'dotnet' @('tests/GeneratedSqlHarness/bin/Debug/net10.0/GeneratedSqlHarness.dll') -Environment @{SQLCMDPASSWORD=$secret;SCHEMORA_SQL_PORT=[string]$port} -TimeoutSeconds 180
 $results=$harness.Output.Trim() | ConvertFrom-Json
 if(-not $results.success -or $results.cases.Count -ne 21){throw 'Generated SQL harness did not complete all 21 cases.'}
 $report.results=$results;$report.status='Passed'
}catch{$failure=$_.Exception.Message.Replace($secret,'[REDACTED]');$report.status='Failed';$report.error=$failure}
finally{
 if($attempted -and (-not $containerId -or $containerId -notmatch '^[0-9a-f]{64}$')){
  try{
   $lookup=Invoke-Tool $DockerPath @('inspect','--format','{{.Id}}|{{ index .Config.Labels "com.schemora.owner" }}',$containerName) -AllowFailure
   if($lookup.ExitCode -eq 0){$identity=$lookup.Output.Trim().Split('|');if($identity.Count -ne 2 -or $identity[1] -ne $owner -or $identity[0] -notmatch '^[0-9a-f]{64}$'){throw 'Ownership mismatch during fallback cleanup.'};$containerId=$identity[0]}
  }catch{$report.cleanupError=$_.Exception.Message.Replace($secret,'[REDACTED]');$report.status='Failed';if(-not $failure){$failure=$report.cleanupError}}
 }
 if($containerId -and $containerId -match '^[0-9a-f]{64}$'){
  try{
   $actualOwner=(Invoke-Tool $DockerPath @('inspect','--format','{{ index .Config.Labels "com.schemora.owner" }}',$containerId)).Output.Trim()
   if($actualOwner -ne $owner){throw 'Refusing cleanup: ownership mismatch.'}
   [void](Invoke-Tool $DockerPath @('rm','--force','--volumes',$containerId))
   $remaining=Invoke-Tool $DockerPath @('inspect','--format','{{.Id}}',$containerId) -AllowFailure
   if($remaining.ExitCode -eq 0){throw 'Owned container still exists after cleanup.'}
   $report.cleanedUp=$true
  }catch{$report.cleanupError=$_.Exception.Message.Replace($secret,'[REDACTED]');$report.status='Failed';if(-not $failure){$failure=$report.cleanupError}}
 }
 $report.completedAtUtc=[datetime]::UtcNow.ToString('O')
 $reportPath=Join-Path $work 'generated-sql-report.json'
 [IO.File]::WriteAllText($reportPath,($report|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
 $secret=$null
}
if($failure){throw $failure}
Write-Host ('PASS: 21 generated-SQL execution cases, including IN list, single-value and decimal cases, including exact 10000 rows and one JSON parameter. Owned container removed. Report: '+$reportPath)
