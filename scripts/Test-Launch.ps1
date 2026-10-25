#Requires -Version 7.0
[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $SkipBuild){& dotnet build (Join-Path $repo 'src/Schemora.Host/Schemora.Host.csproj') -c Debug --nologo;if($LASTEXITCODE -ne 0){throw 'Host build failed'}}
if(-not $Executable){$Executable=Join-Path $repo 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe'}
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$runDirectory=Join-Path $repo ('work/launch-'+[guid]::NewGuid().ToString('N'))
$hostCopy=Join-Path $runDirectory 'host';New-Item -ItemType Directory -Force -Path $hostCopy|Out-Null
$sourceDirectory=Split-Path $Executable -Parent
if(Test-Path -LiteralPath (Join-Path $sourceDirectory 'Schemora.Host.dll')){Get-ChildItem -LiteralPath $sourceDirectory|Copy-Item -Destination $hostCopy -Recurse}
else{Copy-Item -LiteralPath $Executable -Destination $hostCopy}
$testExecutable=Join-Path $hostCopy (Split-Path $Executable -Leaf)
$config=Join-Path $hostCopy 'schemora.json'
$originalConfig=Join-Path $sourceDirectory 'schemora.json'
$originalHash=if(Test-Path -LiteralPath $originalConfig){(Get-FileHash -LiteralPath $originalConfig -Algorithm SHA256).Hash}else{$null}
$repoConfig=Join-Path $repo 'schemora.json';$repoHash=(Get-FileHash -LiteralPath $repoConfig -Algorithm SHA256).Hash
$server=$null;$checks=0;$failure=$null;$held=$null
$report=[ordered]@{success=$false;checks=0;runDirectory=$runDirectory}
function Check([bool]$Condition,[string]$Message){Assert-Schemora $Condition $Message;$script:checks++}
function Start-Case([string[]]$AdditionalArguments=@()){
 return Start-SchemoraTestHost $testExecutable $runDirectory (Join-Path $runDirectory ('data-'+[guid]::NewGuid().ToString('N'))) $AdditionalArguments
}
function Stop-Case($Server){
 if($null -eq $Server){return}
 try{[void](Invoke-Schemora $Server '/api/application/shutdown' 'POST' @{});if(-not $Server.Process.WaitForExit(10000)){throw 'Graceful shutdown timed out'}}
 finally{Stop-SchemoraTestHost $Server}
}
function Failed-Case([string[]]$AdditionalArguments){
 $session=Join-Path $runDirectory ('failed-'+[guid]::NewGuid().ToString('N')+'.json')
 $process=[Diagnostics.Process]::new();$process.StartInfo=[Diagnostics.ProcessStartInfo]::new($testExecutable)
 $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true;$process.StartInfo.WorkingDirectory=$runDirectory
 $process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
 foreach($argument in (@('--no-browser','--session-file',$session,'--data-dir',(Join-Path $runDirectory ('data-'+[guid]::NewGuid().ToString('N'))))+$AdditionalArguments)){$process.StartInfo.ArgumentList.Add($argument)}
 try{
  [void]$process.Start();$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit(30000)){$process.Kill($true);throw 'Expected startup failure but process kept running'}
  Check ($process.ExitCode -ne 0) 'Invalid startup unexpectedly returned success'
  Check (-not (Test-Path -LiteralPath $session)) 'Failed startup published a session'
  return $stdout.Result+$stderr.Result
 }finally{if(-not $process.HasExited){$process.Kill($true)};$process.Dispose()}
}
try{
 # Sidecar is resolved beside EXE, not from this different working directory.
 [IO.File]::WriteAllText($config,'{"port":0}')
 $server=Start-Case
 Check (([uri]$server.Origin).Host -eq '127.0.0.1' -and ([uri]$server.Origin).Port -gt 0) 'Default automatic port is not loopback-only'
 Check ((Invoke-Schemora $server '/api/health').status -eq 'ok') 'Configured launch health failed'
 Check ((Invoke-WebRequest ($server.Origin+'/api/health') -SkipHttpErrorCheck).StatusCode -eq 401) 'Token requirement changed'
 Check ((Invoke-WebRequest ($server.Origin+'/api/health') -Headers @{'X-Schemora-Token'=$server.Headers['X-Schemora-Token'];Origin='https://example.com'} -SkipHttpErrorCheck).StatusCode -eq 403) 'Origin protection changed'
 Check ((Invoke-WebRequest ($server.Origin+'/api/health') -Headers @{'X-Schemora-Token'=$server.Headers['X-Schemora-Token'];Host='attacker.invalid'} -SkipHttpErrorCheck).StatusCode -eq 403) 'Host protection changed'
 Stop-Case $server;$server=$null
 Check ((Get-Content -LiteralPath $config -Raw) -ceq '{"port":0}') 'Launch rewrote the sidecar'

 $free=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$free.Start();$fixedPort=$free.LocalEndpoint.Port;$free.Stop()
 [IO.File]::WriteAllText($config,('{"port":'+$fixedPort+'}'))
 $server=Start-Case;Check (([uri]$server.Origin).Port -eq $fixedPort) 'Fixed JSON port was ignored';Stop-Case $server;$server=$null

 $held=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$held.Start();$heldPort=$held.LocalEndpoint.Port
 [IO.File]::WriteAllText($config,('{"port":'+$heldPort+'}'))
 $output=Failed-Case @()
 Check ($output.Contains('LAUNCH_PORT_IN_USE') -and $output.Contains([string]$heldPort) -and $output.Contains($config)) 'Occupied JSON port did not produce actionable port/config diagnostics'
 $server=Start-Case @('--port','0');Check (([uri]$server.Origin).Port -ne $heldPort) 'CLI automatic override did not override occupied JSON port';Stop-Case $server;$server=$null
 Check ((Get-Content -LiteralPath $config -Raw) -ceq ('{"port":'+$heldPort+'}')) 'CLI override rewrote the sidecar'
 [IO.File]::WriteAllText($config,'{"port":0}')
 $output=Failed-Case @('--port',[string]$heldPort)
 Check ($output.Contains('LAUNCH_PORT_IN_USE') -and $output.Contains([string]$heldPort)) 'Occupied CLI port silently switched to another port'

 $custom=Join-Path $runDirectory 'custom launch.json';[IO.File]::WriteAllText($custom,'{"port":0}')
 [IO.File]::WriteAllText($config,('{"port":'+$heldPort+'}'))
 $server=Start-Case @('--config',$custom);Check (([uri]$server.Origin).Port -ne $heldPort) 'Explicit JSON path was ignored';Stop-Case $server;$server=$null
 Check ((Get-Content -LiteralPath $custom -Raw) -ceq '{"port":0}') 'Explicit launch configuration was modified'
 [IO.File]::WriteAllText($custom,'not-json')
 $output=Failed-Case @('--config',$custom)
 Check ($output.Contains('LAUNCH_CONFIG_INVALID_JSON') -and $output.Contains($custom)) 'Malformed JSON did not report its path'
 $output=Failed-Case @('--config',(Join-Path $runDirectory 'missing.json'))
 Check ($output.Contains('LAUNCH_CONFIG_NOT_FOUND')) 'Missing explicit configuration was silently ignored'

 Remove-Item -LiteralPath $config
 $server=Start-Case;Check (([uri]$server.Origin).Port -gt 0) 'Missing default config did not use a free port';Stop-Case $server;$server=$null
 Check (-not (Test-Path -LiteralPath $config)) 'Missing default config was unexpectedly created'
 Check ((Get-FileHash -LiteralPath $repoConfig -Algorithm SHA256).Hash -eq $repoHash) 'Repository configuration was modified'
 if($originalHash){Check ((Get-FileHash -LiteralPath $originalConfig -Algorithm SHA256).Hash -eq $originalHash) 'Source artifact configuration was modified'}
 $report.success=$true;$report.loopbackOnly=$true;$report.cliPrecedence=$true;$report.portInUseDiagnostic=$true;$report.sourceUnchanged=$true
}catch{$failure=$_.Exception.Message;$report.error=$failure}
finally{
 if($null -ne $held){$held.Stop()}
 if($null -ne $server){Stop-SchemoraTestHost $server}
 $report.checks=$checks;$report.at=[datetime]::UtcNow.ToString('O');$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $repo 'work/launch-report.json')
}
if($failure){throw $failure}
Write-Host "PASS $checks launch configuration/security checks."