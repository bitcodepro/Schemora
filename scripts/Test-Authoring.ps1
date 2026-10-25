[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if(-not $Executable){$Executable=Join-Path $root 'artifacts/win-x64/Schemora.Host.exe'}
$run=Join-Path $root ('work/authoring-'+[guid]::NewGuid().ToString('N'));$data=Join-Path $run 'data';New-Item -ItemType Directory -Path $run|Out-Null
$server=$null;$checks=0
function Check([bool]$Condition,[string]$Message){Assert-Schemora $Condition $Message;$script:checks++}
try{
 $server=Start-SchemoraTestHost $Executable $run $data
 $tutorial=Invoke-Schemora $server '/api/tutorial' POST
 $base='/api/workspaces/'+$tutorial.workspace.id;$recipe=Invoke-Schemora $server ('/api/recipes/'+$tutorial.recipeId)
 $definition=$recipe.definition;$definition.name='Retained synthetic authoring draft'
 $payload=@{definition=$definition;imported=$false;savedAt=[datetime]::UtcNow.ToString('O');inputText=@{Ids='RUNTIME_NOT_FOR_STORAGE'};rendered=@{sql='RENDERED_NOT_FOR_STORAGE'}}
 Invoke-Schemora $server ($base+'/authoring-draft') PUT $payload|Out-Null
 $saved=Invoke-Schemora $server ($base+'/authoring-draft')
 $json=$saved|ConvertTo-Json -Depth 30
 Check ($saved.draft.definition.name -eq $definition.name) 'Draft was not persisted'
 Check (-not $json.Contains('RUNTIME_NOT_FOR_STORAGE') -and -not $json.Contains('RENDERED_NOT_FOR_STORAGE')) 'Runtime payload leaked into stored draft'
 $oldOrigin=$server.Origin;Stop-SchemoraTestHost $server;$server=$null
 $server=Start-SchemoraTestHost $Executable $run $data
 $recovered=Invoke-Schemora $server ($base+'/authoring-draft')
 Check ($recovered.draft.definition.name -eq $definition.name) 'Draft did not survive host restart'
 $comparison=Invoke-Schemora $server ('/api/recipes/'+$tutorial.recipeId+'/versions/1/comparison')
 Check ($comparison.currentVersion -eq 1 -and $comparison.selectedVersion -eq 1 -and $comparison.selected.dependencies.Count -gt 0) 'Comparison did not validate schema dependencies'
 Invoke-Schemora $server ($base+'/authoring-draft') DELETE|Out-Null
 Check ($null -eq (Invoke-Schemora $server ($base+'/authoring-draft')).draft) 'Discard did not remove persisted draft'
 [ordered]@{success=$true;checks=$checks;hostRestart=$true;originChanged=($oldOrigin -ne $server.Origin);runtimeValuesExcluded=$true}|ConvertTo-Json|Set-Content (Join-Path $root 'work/authoring-api-report.json')
 Write-Output "PASS $checks authoring persistence/privacy/restart/comparison checks"
}finally{Stop-SchemoraTestHost $server}
