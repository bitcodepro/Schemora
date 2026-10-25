[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable,[string]$ReportPath='runtime-verification.json',[switch]$AllowDevelopmentTools)
$ErrorActionPreference='Stop'
$tools=@(@('dotnet','node','npm','MSBuild','devenv')|ForEach-Object {$command=Get-Command $_ -ErrorAction SilentlyContinue;if($command){$command.Source}})
$sdkDirectories=@(@((Join-Path $env:ProgramFiles 'dotnet/sdk'),(Join-Path ${env:ProgramFiles(x86)} 'dotnet/sdk'),(Join-Path $env:ProgramFiles 'nodejs'))|Where-Object {Test-Path -LiteralPath $_})
if(-not $AllowDevelopmentTools -and ($tools.Count -gt 0 -or $sdkDirectories.Count -gt 0)){throw 'This is not a clean runtime machine: development tools were detected. No clean-Windows success is claimed.'}
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
$run=Join-Path $env:TEMP ('schemora-runtime-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $run|Out-Null
$server=$null
try{
 $server=Start-SchemoraTestHost (Resolve-Path -LiteralPath $Executable).Path $run (Join-Path $run 'data')
 $health=Invoke-Schemora $server '/api/health';Assert-Schemora ($health.status -eq 'ok') 'Self-contained host did not start'
 $html=Invoke-WebRequest -Uri $server.Origin;Assert-Schemora ($html.Content -match '<html') 'Embedded UI unavailable'
 $demo=Invoke-Schemora $server '/api/tutorial' POST @{};Assert-Schemora ([bool]$demo.recipeId) 'Tutorial schema and recipe unavailable without SDK'
 $render=Invoke-Schemora $server ('/api/recipes/'+$demo.recipeId+'/render') POST @{inputText=@{Ids='1,2,1'};mode='parameterized';strategy='openjson'}
 Assert-Schemora ($render.parameters.Count -eq 1 -and $render.dapperExample -match 'DynamicParameters') 'Runtime generation failed'
 [ordered]@{success=$true;cleanMachine=(-not $AllowDevelopmentTools);developmentToolsDetected=$tools;sdkDirectoriesDetected=$sdkDirectories;checks=4;version=$health.version;build=$health.build;at=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath $ReportPath
}finally{Stop-SchemoraTestHost $server}
