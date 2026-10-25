[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    Push-Location (Join-Path $taskRoot 'ui')
    try {
        & npm ci --cache (Join-Path $taskRoot 'work/npm-cache')
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
    } finally { Pop-Location }
    & dotnet publish -m:1 src/Schemora.Host/Schemora.Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o artifacts/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    $taskFiles = @(Get-ChildItem -LiteralPath artifacts/win-x64 -File -Recurse)
    $expected = @('Schemora.Host.exe', 'schemora.json')
    if ($taskFiles.Count -ne 2 -or @($taskFiles | Where-Object { $_.DirectoryName -ne (Resolve-Path artifacts/win-x64).Path -or $_.Name -notin $expected }).Count -gt 0) {
        throw ('Expected exactly Schemora.Host.exe + schemora.json, got: ' + ($taskFiles.Name -join ', '))
    }
    Write-Output ('Published: ' + (Join-Path (Resolve-Path artifacts/win-x64).Path 'Schemora.Host.exe'))
    Write-Output 'Launch configuration: schemora.json (existing file preserved; 0 selects a free port).'
} finally { Pop-Location }


