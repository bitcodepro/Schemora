[CmdletBinding()]
param([switch]$Docker)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$run=Join-Path $root ('work/clean-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $run 'source'
New-Item -ItemType Directory -Path $source -Force|Out-Null
$excluded=@('.git','.vs','.idea','bin','obj','node_modules','dist','work','artifacts')
$inventory=[Collections.Generic.List[object]]::new()
$queue=[Collections.Generic.Queue[string]]::new()
$queue.Enqueue($root)
while($queue.Count -gt 0){
    $directory=$queue.Dequeue()
    foreach($entry in Get-ChildItem -LiteralPath $directory -Force){
        if(($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Clean verification refuses linked source entries'}
        if($entry.PSIsContainer){if($entry.Name -notin $excluded){$queue.Enqueue($entry.FullName)};continue}
        if($entry.Extension -in @('.log','.user','.suo')){continue}
        if($inventory.Count -ge 25000){throw 'Source inventory exceeds the clean verification limit'}
        $relative=[IO.Path]::GetRelativePath($root,$entry.FullName)
        $hash=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash
        $destination=Join-Path $source $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
        Copy-Item -LiteralPath $entry.FullName -Destination $destination
        if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash){throw 'Source changed during clean staging'}
        $inventory.Add([ordered]@{path=$relative;sha256=$hash})
    }
}
foreach($item in $inventory){
    if((Get-FileHash -LiteralPath (Join-Path $root $item.path) -Algorithm SHA256).Hash -ne $item.sha256){throw 'Source changed before clean staging finished'}
}
$inventory|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'source-manifest.json')
Push-Location $source
try{
    Push-Location ui
    try{
        & npm ci --cache (Join-Path $root 'work/npm-cache')
        if($LASTEXITCODE -ne 0){throw 'Clean npm ci failed'}
        foreach($test in @('test:graph','test:recipes','test:enhancements','test:i18n','build')){
            & npm run $test
            if($LASTEXITCODE -ne 0){throw ('Clean UI '+$test+' failed')}
        }
    }finally{Pop-Location}
    & dotnet build -m:1 Schemora.slnx -c Release --nologo
    if($LASTEXITCODE -ne 0){throw 'Clean solution build failed'}
    & dotnet test -m:1 Schemora.slnx -c Release --no-build --no-restore --nologo --logger trx --results-directory work/test-results
    if($LASTEXITCODE -ne 0){throw 'Clean .NET tests failed'}
    & pwsh -NoProfile -File scripts/Publish.ps1
    if($LASTEXITCODE -ne 0){throw 'Clean publish failed'}
    $parameters=@('-NoProfile','-File','scripts/Test-All.ps1')
    if($Docker){$parameters+='-Docker'}
    & pwsh @parameters
    if($LASTEXITCODE -ne 0){throw 'Clean published executable regressions failed'}
    $exe=Join-Path $source 'artifacts/win-x64/Schemora.Host.exe'
    [ordered]@{success=$true;sourceDirectory=$source;sourceFileCount=$inventory.Count;sourceManifest=(Join-Path $run 'source-manifest.json');executable=$exe;sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;docker=[bool]$Docker;at=[datetime]::UtcNow.ToString('O');remoteCiRun=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'work/clean-report.json')
    Write-Host ('PASS clean Windows source build, tests, publish and acceptance: '+$source)
}finally{Pop-Location}



