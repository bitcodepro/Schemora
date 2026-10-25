# -Group selects one suite group (see scripts/README.md). With the default All, -SkipUi drops the browser
# group and -Docker adds the Docker group, as before. Core and Ui suites start their own hosts with private data
# directories and free ports, so up to -Throttle of them run at once; Docker suites share the daemon and run in order.
[CmdletBinding()]
param(
    [string]$Executable,
    [ValidateSet('All','Core','Ui','Docker')][string]$Group='All',
    [switch]$SkipUi,
    [switch]$Docker,
    [ValidateRange(1,16)][int]$Throttle=4
)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
if(-not $Executable){$Executable=Join-Path $root 'artifacts/win-x64/Schemora.Host.exe'}
$Executable=(Resolve-Path -LiteralPath $Executable).Path
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
Remove-OldTestRuns (Join-Path $root 'work')
$groups=@()
if($Group -in @('All','Core')){$groups+='Core'}
if(($Group -eq 'Ui') -or ($Group -eq 'All' -and -not $SkipUi)){$groups+='Ui'}
if(($Group -eq 'Docker') -or ($Group -eq 'All' -and $Docker)){$groups+='Docker'}
$logs=Join-Path $root ('work/test-all-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $logs | Out-Null
$started=Get-Date

function Suite([string]$Script,[string[]]$Arguments) { [pscustomobject]@{ Script=$Script; Arguments=$Arguments } }
function Invoke-Suites($Suites,[int]$Limit) {
    $results=$Suites | ForEach-Object -ThrottleLimit $Limit -Parallel {
        $log=Join-Path $using:logs ($_.Script+'.log')
        $watch=[Diagnostics.Stopwatch]::StartNew()
        & pwsh -NoProfile -File (Join-Path $using:PSScriptRoot $_.Script) @($_.Arguments) *> $log
        [pscustomobject]@{ Script=$_.Script; Exit=$LASTEXITCODE; Seconds=[int]$watch.Elapsed.TotalSeconds; Log=$log }
    }
    $failed=@()
    foreach($result in $results | Sort-Object Script){
        $summary=@(Get-Content -LiteralPath $result.Log | Where-Object { $_ -match '^PASS' }) | Select-Object -Last 1
        if($result.Exit -eq 0){ Write-Host ('{0,-28} {1,4}s  {2}' -f $result.Script,$result.Seconds,$summary) }
        else{
            $failed+=$result
            Write-Host ('{0,-28} {1,4}s  FAILED (exit {2}); log: {3}' -f $result.Script,$result.Seconds,$result.Exit,$result.Log)
            Get-Content -LiteralPath $result.Log -Tail 25 | ForEach-Object { Write-Host ('    '+$_) }
        }
    }
    if($failed.Count){ throw ('Failed suites: '+(($failed | ForEach-Object Script) -join ', ')+'. Logs are kept in '+$logs) }
}

if($groups -contains 'Core'){
    $core=@(Suite 'Test-Smoke.ps1' @('-Executable',$Executable))
    foreach($name in @('Launch','Workspace','Relationships','Dapper','Recipes','Changes','Release','Enhancements','ProductV13','MaintenanceV13','Authoring','Professional','Hardening','Scale')){
        $core+=Suite ('Test-'+$name+'.ps1') @('-Executable',$Executable,'-SkipBuild')
    }
    Invoke-Suites $core $Throttle
}
if($groups -contains 'Ui'){
    $ui=@('Test-Ui.ps1','Test-Localization.ps1','Test-ProductV13Ui.ps1','Test-Experience.ps1','Test-ProfessionalUi.ps1','Test-HardeningUi.ps1') |
        ForEach-Object { Suite $_ @('-Executable',$Executable,'-SkipBuild') }
    Invoke-Suites $ui $Throttle
}
if($groups -contains 'Docker'){
    Invoke-Suites @(
        (Suite 'Test-GeneratedSql.ps1' @('-Run')),
        (Suite 'Test-DockerSchema.ps1' @('-Run','-Executable',$Executable,'-SkipBuild')),
        (Suite 'Test-DockerFailure.ps1' @('-Run','-Executable',$Executable))
    ) 1
}
Remove-Item -LiteralPath $logs -Recurse -Force
Write-Host ('PASS Schemora regression suites: '+($groups -join ', ')+' in '+[int]((Get-Date)-$started).TotalMinutes+' min')
