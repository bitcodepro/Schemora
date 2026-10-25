[CmdletBinding()]
param([string]$Executable, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $taskRoot 'artifacts/win-x64/Schemora.Host.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$taskRun = Join-Path $taskRoot ('work/smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRun -Force | Out-Null
# Copy only the EXE. The current directory deliberately contains no UI/DLL dependencies.
$taskExe = Join-Path $taskRun 'Schemora.Host.exe'
Copy-Item -LiteralPath $Executable -Destination $taskExe
$taskSessionPath = Join-Path $taskRun 'session.json'
$taskProcess = [System.Diagnostics.Process]::new()
$taskProcess.StartInfo = [System.Diagnostics.ProcessStartInfo]::new($taskExe)
$taskProcess.StartInfo.UseShellExecute = $false
$taskProcess.StartInfo.CreateNoWindow = $true
$taskProcess.StartInfo.WorkingDirectory = $taskRun
$taskProcess.StartInfo.RedirectStandardOutput = $true
$taskProcess.StartInfo.RedirectStandardError = $true
foreach ($taskArgument in @('--no-browser','--session-file',$taskSessionPath,'--data-dir',(Join-Path $taskRun 'data'))) { $taskProcess.StartInfo.ArgumentList.Add($taskArgument) }
$taskProcess.Start() | Out-Null
$taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
$taskStderr = $taskProcess.StandardError.ReadToEndAsync()
function Assert-Task([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-TaskState([string]$Path) {
    return (@(Get-ChildItem -LiteralPath $Path -Recurse -File | ForEach-Object { $_.FullName.Substring($Path.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join '|')
}
try {
    $taskDeadline = [datetime]::UtcNow.AddSeconds(40)
    while (-not (Test-Path -LiteralPath $taskSessionPath)) {
        if ($taskProcess.HasExited) { throw ('Host exited: ' + $taskStdout.Result + $taskStderr.Result) }
        if ([datetime]::UtcNow -gt $taskDeadline) { throw 'Host startup timed out' }
        Start-Sleep -Milliseconds 200
    }
    $taskSession = Get-Content -LiteralPath $taskSessionPath -Raw | ConvertFrom-Json
    $taskHeaders = @{ 'X-Schemora-Token' = $taskSession.token; Origin = $taskSession.origin }
    $taskBase = $taskSession.origin
    $taskHealth = Invoke-WebRequest "$taskBase/api/health" -Headers $taskHeaders
    Assert-Task ($taskHealth.StatusCode -eq 200) 'Authorized health failed'
    $taskNoToken = Invoke-WebRequest "$taskBase/api/health" -SkipHttpErrorCheck
    Assert-Task ($taskNoToken.StatusCode -eq 401) 'Missing token accepted'
    $taskBadToken = Invoke-WebRequest "$taskBase/api/health" -Headers @{ 'X-Schemora-Token' = 'wrong' } -SkipHttpErrorCheck
    Assert-Task ($taskBadToken.StatusCode -eq 401) 'Invalid token accepted'
    $taskBadOrigin = Invoke-WebRequest "$taskBase/api/health" -Headers @{ 'X-Schemora-Token' = $taskSession.token; Origin = 'https://example.com' } -SkipHttpErrorCheck
    Assert-Task ($taskBadOrigin.StatusCode -eq 403) 'Foreign origin accepted'
    $taskBadHost = Invoke-WebRequest "$taskBase/api/health" -Headers @{ 'X-Schemora-Token' = $taskSession.token; Host = 'attacker.invalid' } -SkipHttpErrorCheck
    Assert-Task ($taskBadHost.StatusCode -eq 403) 'Foreign Host accepted'
    $taskCrossSite = Invoke-WebRequest "$taskBase/api/health" -Headers @{ 'X-Schemora-Token' = $taskSession.token; 'Sec-Fetch-Site' = 'cross-site' } -SkipHttpErrorCheck
    Assert-Task ($taskCrossSite.StatusCode -eq 403) 'Cross-site request accepted'
    $taskHtml = Invoke-WebRequest "$taskBase/"
    Assert-Task ($taskHtml.Content -match '<div id="root">') 'Embedded React shell missing'
    $taskAssets = [regex]::Matches($taskHtml.Content, '(?:src|href)="(/assets/[^"]+)"')
    Assert-Task ($taskAssets.Count -ge 2) 'Embedded JS/CSS links missing'
    foreach ($taskAsset in $taskAssets) {
        $taskAssetResponse = Invoke-WebRequest ($taskBase + $taskAsset.Groups[1].Value)
        Assert-Task ($taskAssetResponse.StatusCode -eq 200 -and $taskAssetResponse.RawContentLength -gt 100) 'Embedded asset missing'
    }
    $taskDiagnostics = Invoke-RestMethod "$taskBase/api/diagnostics" -Headers $taskHeaders
    Assert-Task ($taskDiagnostics.capabilities.Count -ge 5) 'Toolchain diagnostics incomplete'
    $taskFixture = Join-Path $taskRoot 'tests/Fixtures/Commerce/Commerce.sqlproj'
    $taskDenied = Invoke-WebRequest "$taskBase/api/schema/build" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = $taskFixture; trustProject = $false } | ConvertTo-Json) -SkipHttpErrorCheck
    Assert-Task ($taskDenied.StatusCode -eq 403) 'Untrusted build accepted'
    $taskInvalid = Invoke-WebRequest "$taskBase/api/schema/import" -Method Post -Headers $taskHeaders -ContentType application/json -Body '{"path":"relative.dacpac"}' -SkipHttpErrorCheck
    Assert-Task ($taskInvalid.StatusCode -eq 400) 'Relative file accepted'
    $taskCorrupt = Join-Path $taskRun 'broken.dacpac'
    Set-Content -LiteralPath $taskCorrupt -Value 'not a DACPAC'
    $taskCorruptResponse = Invoke-WebRequest "$taskBase/api/schema/import" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = $taskCorrupt } | ConvertTo-Json) -SkipHttpErrorCheck
    Assert-Task ($taskCorruptResponse.StatusCode -eq 422) 'Corrupt DACPAC did not fail explicitly'
    $taskDacpac = Join-Path $taskRoot 'tests/Fixtures/Commerce/bin/Release/Commerce.dacpac'
    if (-not (Test-Path -LiteralPath $taskDacpac)) {
        & dotnet build $taskFixture -c Release --nologo
        Assert-Task ($LASTEXITCODE -eq 0) 'Fixture build failed'
    }
    $taskGraph = Invoke-RestMethod "$taskBase/api/schema/import" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = $taskDacpac } | ConvertTo-Json)
    Assert-Task ($taskGraph.tables.Count -eq 4 -and $taskGraph.relations.Count -eq 1) 'DacFx extraction in single EXE failed'
    Assert-Task ($taskGraph.relations[0].fromColumns.Count -eq 2) 'Composite FK columns lost'
    $taskLegacyStatus = 'Skipped'
    $taskLegacyTableCount = 0
    if (-not $SkipBuild) {
        $taskBefore = Get-TaskState (Split-Path -Parent $taskFixture)
        $taskBuilt = Invoke-RestMethod "$taskBase/api/schema/build" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = $taskFixture; trustProject = $true } | ConvertTo-Json)
        Assert-Task ($taskBuilt.tables.Count -eq 4) 'SDK project -> build -> DACPAC -> graph failed'
        $taskAfter = Get-TaskState (Split-Path -Parent $taskFixture)
        Assert-Task ($taskBefore -eq $taskAfter) 'API build modified the source repository'
        # Regression: absolute output paths must not write into the source; stale input DACPAC must never win.
        $taskRegression = Join-Path $taskRun 'input'
        New-Item -ItemType Directory -Path $taskRegression | Out-Null
        Copy-Item -LiteralPath $taskFixture -Destination (Join-Path $taskRegression 'Commerce.sqlproj')
        Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $taskFixture) 'Schema.sql') -Destination $taskRegression
        $taskForbiddenOutput = Join-Path $taskRegression 'original-output'
        $taskRegressionProject = Join-Path $taskRegression 'Commerce.sqlproj'
        $taskProjectXml = [xml](Get-Content -LiteralPath $taskRegressionProject -Raw)
        $taskProjectXml.Project.PropertyGroup.Name = 'FreshCommerce'
        foreach ($taskProperty in @('OutputPath','BaseIntermediateOutputPath','IntermediateOutputPath','MSBuildProjectExtensionsPath')) {
            $taskElement = $taskProjectXml.CreateElement($taskProperty)
            $taskElement.InnerText = $taskForbiddenOutput + [IO.Path]::DirectorySeparatorChar
            $taskProjectXml.Project.PropertyGroup.AppendChild($taskElement) | Out-Null
        }
        $taskProjectXml.Save($taskRegressionProject)
        Set-Content -LiteralPath (Join-Path $taskRegression 'Commerce.dacpac') -Value 'stale invalid package; must not be selected'
        $taskRegressionBefore = Get-TaskState $taskRegression
        $taskFreshGraph = Invoke-RestMethod "$taskBase/api/schema/build" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = $taskRegressionProject; trustProject = $true } | ConvertTo-Json)
        Assert-Task ($taskFreshGraph.tables.Count -eq 4) 'Stale DACPAC chosen over fresh build output'
        Assert-Task (-not (Test-Path -LiteralPath $taskForbiddenOutput)) 'Absolute project output path modified the source'
        Assert-Task ($taskRegressionBefore -eq (Get-TaskState $taskRegression)) 'Regression project source changed during build'
        # Ordinary false Exists imports are supported; dynamic property functions are explicitly incomplete.
        Set-Content -LiteralPath (Join-Path $taskRun 'Directory.Build.props') -Value '<Project><Import Project="build/optional.props" Condition="Exists(''build/optional.props'')" /></Project>'
        $taskOptionalGraph = Invoke-RestMethod "$taskBase/api/schema/build" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = $taskRegressionProject; trustProject = $true } | ConvertTo-Json)
        Assert-Task ($taskOptionalGraph.tables.Count -eq 4) 'Ordinary conditional import was rejected'
        Set-Content -LiteralPath (Join-Path $taskRun 'Directory.Build.props') -Value '<Project><Import Project="$([System.Environment]::GetEnvironmentVariable(''SCHEMORA_SYNTHETIC_IMPORT''))" /></Project>'
        $taskContextResponse = Invoke-WebRequest "$taskBase/api/schema/build" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = $taskRegressionProject; trustProject = $true } | ConvertTo-Json) -SkipHttpErrorCheck
        Assert-Task ($taskContextResponse.StatusCode -eq 422 -and ($taskContextResponse.Content | ConvertFrom-Json).code -eq 'BUILD_INPUTS_INCOMPLETE' -and $taskContextResponse.Content.Contains('BUILD_DYNAMIC_INPUT_UNRESOLVED')) 'Dynamic import was silently omitted'
        Remove-Item -LiteralPath (Join-Path $taskRun 'Directory.Build.props')
        $taskLegacy = Invoke-WebRequest "$taskBase/api/schema/build" -Method Post -Headers $taskHeaders -ContentType application/json -Body (@{ path = (Join-Path $taskRoot 'tests/Fixtures/Legacy/Legacy.sqlproj'); trustProject = $true } | ConvertTo-Json) -SkipHttpErrorCheck
        Assert-Task ($taskLegacy.StatusCode -eq 200 -or ($taskLegacy.StatusCode -eq 422 -and ($taskLegacy.Content | ConvertFrom-Json).code -eq 'SSDT_TOOLCHAIN_MISSING')) 'Legacy build did not return supported toolchain outcome'
        if ($taskLegacy.StatusCode -eq 200) {
            $taskLegacyGraph = $taskLegacy.Content | ConvertFrom-Json
            Assert-Task ($taskLegacyGraph.tables.Count -eq 1) 'Legacy build metadata missing'
            $taskLegacyStatus = 'BuiltAndAnalyzed'
            $taskLegacyTableCount = $taskLegacyGraph.tables.Count
        } else { $taskLegacyStatus = 'MissingToolchainDiagnostic' }
    }
    $taskGraph | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $taskRun 'graph.json')
    [ordered]@{ success = $true; origin = $taskBase; executable = $taskExe; tables = $taskGraph.tables.Count; relations = $taskGraph.relations.Count; securityChecks = 6; embeddedAssets = $taskAssets.Count; sourceUnchanged = (-not $SkipBuild); legacyBuildStatus = $taskLegacyStatus; legacyTables = $taskLegacyTableCount; capabilities = $taskDiagnostics.capabilities } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskRoot 'work/smoke-report.json')
    Write-Output 'PASS: single EXE, embedded UI, DacFx graph, SDK build, legacy diagnostics, source immutability and local API security.'
} finally {
    if (-not $taskProcess.HasExited) { $taskProcess.Kill($true); $taskProcess.WaitForExit() }
    $taskProcess.Dispose()
    if (Test-Path -LiteralPath $taskSessionPath) { Remove-Item -LiteralPath $taskSessionPath -Force }
}



