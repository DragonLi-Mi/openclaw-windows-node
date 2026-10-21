[CmdletBinding()]
param([string]$RepoRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
. (Join-Path $PSScriptRoot 'ProofEvidence.ps1')
$root = Join-Path $RepoRoot ('TestResults\proof-contract-' + [guid]::NewGuid().ToString('N'))
$sourceProbe = Join-Path $RepoRoot ('scripts\proof-source-probe-' + [guid]::NewGuid().ToString('N') + '.txt')
$artifactRoot = Join-Path $RepoRoot ('artifacts\proof-contract-' + [guid]::NewGuid().ToString('N'))
$unsupportedRoot = Join-Path $RepoRoot ('proof-output-' + [guid]::NewGuid().ToString('N'))
$lookalikeRoot = Join-Path $RepoRoot ('TestResults-lookalike-' + [guid]::NewGuid().ToString('N'))
$shell = (Get-Process -Id $PID).Path
$checks = 0
$runnerInvocations = 0
New-Item -ItemType Directory -Path $root | Out-Null

function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Reason) { throw "Unexpected rejection (wanted $Reason): $($_.Exception.Message)" }
        $script:checks++
        return
    }
    throw "Expected rejection: $Reason"
}

function Write-Trx([string]$Path, [int]$Passed = 1, [int]$Skipped = 0, [int]$Failed = 0, [DateTime]$Time = [DateTime]::UtcNow) {
    $stamp = $Time.ToUniversalTime().ToString('o')
    $results = ''
    foreach ($outcome in @(@('Passed') * $Passed) + @(@('NotExecuted') * $Skipped) + @(@('Failed') * $Failed)) {
        $results += "<UnitTestResult executionId='$([guid]::NewGuid())' outcome='$outcome' />"
    }
    $total = $Passed + $Skipped + $Failed
    $executed = $Passed + $Failed
    $text = "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Times start='$stamp' finish='$stamp'/><Results>$results</Results><ResultSummary outcome='Completed'><Counters total='$total' executed='$executed' passed='$Passed' failed='$Failed' notExecuted='$Skipped' error='0'/></ResultSummary></TestRun>"
    Set-Content -LiteralPath $Path -Value $text -Encoding UTF8
}

try {
    $invocation = New-TestInvocation -ResultsDirectory $root -TrxFileName 'winui-accessibility.trx'
    $report = $invocation.report
    $start = [DateTime]::Parse($invocation.startedUtc).ToUniversalTime()
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'missing'
    Set-Content $report '<not-xml'
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'Load|XML|end of file|unexpected'
    Set-Content $report '<TestRun/>'
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'namespace'
    Write-Trx $report 0
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'zero'
    Write-Trx $report 0 1
    Assert-Rejected { Read-TestResultContract $report $start 0 -AllowSkipped } 'zero'
    Write-Trx $report 1 1
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'Skips'
    $mixed = Read-TestResultContract $report $start 0 -AllowSkipped
    if ($mixed.outcome -ne 'passed-with-permitted-skips' -or $mixed.executed -ne 1 -or $mixed.skipped -ne 1) { throw 'Ordinary CI skip policy lost.' }
    $checks++
    (Get-Content $report -Raw).Replace("notExecuted='1'", "notExecuted='0'") | Set-Content $report
    $vstestMixed = Read-TestResultContract $report $start 0 -AllowSkipped
    if ($vstestMixed.executed -ne 1 -or $vstestMixed.skipped -ne 1) { throw 'Actual VSTest skipped-row format was rejected or lost skips.' }
    $checks++
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'Skips'
    (Get-Content $report -Raw).Replace("notExecuted='0'", "notExecuted='2'") | Set-Content $report
    Assert-Rejected { Read-TestResultContract $report $start 0 -AllowSkipped } 'counter'
    Write-Trx $report 1 0 1
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'failed tests'
    Write-Trx $report
    Assert-Rejected { Read-TestResultContract $report $start 7 } 'exit code 7'
    Write-Trx $report 1 0 0 ([DateTime]::UtcNow.AddDays(-1))
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'stale'
    Write-Trx $report
    (Get-Item $report).LastWriteTimeUtc = $start.AddMinutes(-1)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'stale'
    Write-Trx $report
    foreach ($lagMilliseconds in @(10, 1000)) {
        (Get-Item $report).LastWriteTimeUtc = $start.AddMilliseconds(-$lagMilliseconds)
        $fresh = Read-TestResultContract $report $start 0
        if ($fresh.executed -ne 1 -or $fresh.outcome -ne 'passed') { throw 'Fresh report with filesystem clock granularity was rejected.' }
        $checks++
    }
    (Get-Item $report).LastWriteTimeUtc = $start.AddMilliseconds(-1001)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'stale'
    Write-Trx $report 1 0 0 $start.AddSeconds(-2)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'TRX start time'
    foreach ($replacement in @(
        @{ old = "total='1'"; new = "total='2'" },
        @{ old = "passed='1'"; new = "passed='-1'" },
        @{ old = "error='0'"; new = "error='1'" },
        @{ old = "executed='1'"; new = "executed='bogus'" }
    )) {
        Write-Trx $report
        (Get-Content $report -Raw).Replace($replacement.old, $replacement.new) | Set-Content $report
        Assert-Rejected { Read-TestResultContract $report $start 0 } 'counter'
    }
    Write-Trx $report
    $valid = Read-TestResultContract $report $start 0
    if ($valid.executed -ne 1 -or $valid.outcome -ne 'passed') { throw 'Fresh all-pass report failed.' }
    $checks++

    # Modeled grouped-leaf contract. Current Shared/Tray/CLI VSTest reports
    # were checked separately and contain flat results, not InnerResults.
    Write-Trx $report 2
    [xml]$grouped = Get-Content $report -Raw
    $parent = $grouped.CreateElement('UnitTestResult', $grouped.DocumentElement.NamespaceURI)
    $parent.SetAttribute('executionId', [guid]::NewGuid().ToString())
    $parent.SetAttribute('outcome', 'Passed')
    $inner = $grouped.CreateElement('InnerResults', $grouped.DocumentElement.NamespaceURI)
    $resultContainer = $grouped.DocumentElement.SelectSingleNode("*[local-name()='Results']")
    foreach ($leaf in @($resultContainer.ChildNodes)) { [void]$inner.AppendChild($leaf) }
    [void]$parent.AppendChild($inner)
    [void]$resultContainer.AppendChild($parent)
    $grouped.Save($report)
    $groupResult = Read-TestResultContract $report $start 0
    if ($groupResult.executed -ne 2 -or $groupResult.discovered -ne 2) { throw 'Grouped data-driven results were not counted as leaf cases.' }
    $checks++
    Write-Trx $report 2
    [xml]$duplicates = Get-Content $report -Raw
    foreach ($leaf in $duplicates.TestRun.Results.UnitTestResult) { $leaf.SetAttribute('executionId', 'duplicate') }
    $duplicates.Save($report)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'identities'
    Write-Trx $report

    $gatewayArguments = @{
        RepoRoot = $RepoRoot
        Project = 'tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj'
        Filter = 'FullyQualifiedName~OpenClaw.E2ETests.Setup.SetupAndConnectTests'
        ResultName = 'gateway-setup-connect'
        RuntimeIdentifier = 'win-x64'
    }
    $previousGate = [Environment]::GetEnvironmentVariable('OPENCLAW_RUN_E2E')
    $previousOllamaGate = [Environment]::GetEnvironmentVariable('OPENCLAW_RUN_OLLAMA_E2E')
    try {
        $env:OPENCLAW_RUN_E2E = $null
        $env:OPENCLAW_RUN_OLLAMA_E2E = $null
        Assert-Rejected { Get-ProofCommandBinding @gatewayArguments } 'requires OPENCLAW_RUN_E2E=1'
        $env:OPENCLAW_RUN_E2E = '1'
        Assert-Rejected { Get-ProofCommandBinding @gatewayArguments } 'requires OPENCLAW_RUN_OLLAMA_E2E=1'
        $env:OPENCLAW_RUN_OLLAMA_E2E = '1'
        $gatewayBinding = Get-ProofCommandBinding @gatewayArguments
        if ($gatewayBinding.environment.Count -ne 2 -or $gatewayBinding.environment.OPENCLAW_RUN_E2E -cne '1' -or
            $gatewayBinding.environment.OPENCLAW_RUN_OLLAMA_E2E -cne '1') { throw 'Gateway binding omitted a required selected-test gate.' }
        $checks++
        $gatewayInvocation = New-TestInvocation -ResultsDirectory $root -TrxFileName 'gateway-setup-connect.trx'
        Write-Trx $gatewayInvocation.report
        $gatewayResult = Read-TestResultContract $gatewayInvocation.report ([DateTime]$gatewayInvocation.startedUtc) 0
        Write-ProofEvidence $RepoRoot $gatewayBinding $gatewayInvocation (Get-ProofSourceIdentity $RepoRoot) 0 $gatewayResult ''
        $gatewaySummaryPath = Join-Path $gatewayInvocation.directory 'evidence-summary.json'
        $gatewaySummaryText = Get-Content $gatewaySummaryPath -Raw
        $env:OPENCLAW_RUN_E2E = $null
        $env:OPENCLAW_RUN_OLLAMA_E2E = $null
        $gateChecked = Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId)
        if (-not $gateChecked.validExecutionEvidence) { throw 'Recorded environment was incorrectly tied to the validating shell.' }
        $checks++
        $tamperedGate = $gatewaySummaryText | ConvertFrom-Json
        $tamperedGate.environment.OPENCLAW_RUN_E2E = '0'
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'environment gate'
        $tamperedGate = $gatewaySummaryText | ConvertFrom-Json
        $tamperedGate.environment.OPENCLAW_RUN_OLLAMA_E2E = '0'
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'OPENCLAW_RUN_OLLAMA_E2E=1 environment gate'
        $tamperedGate.environment.PSObject.Properties.Remove('OPENCLAW_RUN_OLLAMA_E2E')
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'environment observations'
        $tamperedGate = $gatewaySummaryText | ConvertFrom-Json
        $tamperedGate.environment | Add-Member -NotePropertyName 'PRIVATE_VALUE' -NotePropertyValue 'not-permitted'
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'undeclared environment'
        $tamperedGate.PSObject.Properties.Remove('environment')
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'environment observations'
    } finally {
        $env:OPENCLAW_RUN_E2E = $previousGate
        $env:OPENCLAW_RUN_OLLAMA_E2E = $previousOllamaGate
    }

    $binding = Get-ProofCommandBinding $RepoRoot 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'Category=Accessibility' 'winui-accessibility' 'win-x64'
    if ($binding.poolId -ne 'windows-winui-interactive') { throw 'Inventory command binding failed.' }
    $source = Get-ProofSourceIdentity $RepoRoot
    $capture = Join-Path $root 'owned-window.png'
    [IO.File]::WriteAllBytes($capture, [byte[]]@(1, 2, 3))
    $receipt = Join-Path $root 'run.json'
    Set-Content $receipt '{"fixtureReceipt":"synthetic; digest reference only"}'
    $binary = Join-Path $root 'fixture.dll'
    [IO.File]::WriteAllBytes($binary, [byte[]]@(4, 5, 6))
    $artifacts = @((Get-ProofArtifact $capture $RepoRoot 'capture'), (Get-ProofArtifact $receipt $RepoRoot 'fixture-receipt'), (Get-ProofArtifact $binary $RepoRoot 'binary'))
    Write-ProofEvidence $RepoRoot $binding $invocation $source 0 $valid '' $artifacts
    $summaryPath = Join-Path $invocation.directory 'evidence-summary.json'
    $summaryText = Get-Content $summaryPath -Raw
    $checked = Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId)
    if (-not $checked.validExecutionEvidence -or $checked.completePoolProof) { throw 'Evidence must not claim a complete pool.' }
    $checks++
    Assert-Rejected { Assert-ProofResultsDirectory $unsupportedRoot $RepoRoot } 'under TestResults or artifacts'
    Assert-Rejected { Assert-ProofResultsDirectory $lookalikeRoot $RepoRoot } 'under TestResults or artifacts'
    Assert-Rejected { Assert-ProofResultsDirectory $RepoRoot $RepoRoot } 'under TestResults or artifacts'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @((Join-Path $unsupportedRoot 'evidence-summary.json')) @($binding.poolId) } 'under TestResults or artifacts'
    Assert-Rejected { Write-ProofEvidence $RepoRoot $binding @{ directory = $unsupportedRoot } $source 0 $valid '' } 'under TestResults or artifacts'
    $cli = (& (Join-Path $RepoRoot 'scripts\validate-proof-evidence.ps1') -RepoRoot $RepoRoot -SummaryPath $summaryPath -RequiredPoolId $binding.poolId) | ConvertFrom-Json
    if (-not $cli.validExecutionEvidence -or $cli.completePoolProof) { throw 'Read-only evidence CLI overclaims proof.' }
    $checks++
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @('windows-wsl-mxc') } 'Required pool'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) @('windows-winui-interactive/exercise-changed-ui') } 'Required command'
    Set-Content $capture 'tampered'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'digest mismatch'
    [IO.File]::WriteAllBytes($capture, [byte[]]@(1, 2, 3))
    Set-Content $binary 'tampered binary'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'digest mismatch'
    [IO.File]::WriteAllBytes($binary, [byte[]]@(4, 5, 6))
    Set-Content $sourceProbe 'new relevant source'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'source identity is stale'
    Assert-Rejected { Write-ProofEvidence $RepoRoot $binding $invocation $source 0 $valid '' $artifacts } 'Source changed'
    Remove-Item $sourceProbe
    $blocked = Get-Content $summaryPath -Raw | ConvertFrom-Json
    if ($blocked.outcome -ne 'blocked' -or $blocked.blockedReason -ne 'source-changed-during-run') { throw 'Changed source emitted passing evidence.' }
    $checks++
    Set-Content $summaryPath $summaryText
    foreach ($case in @('head', 'pool', 'command', 'blocked', 'counts', 'assurance')) {
        $changed = $summaryText | ConvertFrom-Json
        switch ($case) {
            'head' { $changed.source.head = '0' * 40 }
            'pool' { $changed.poolId = 'missing-pool' }
            'command' { $changed.command = 'private arbitrary shell text' }
            'blocked' { $changed.outcome = 'blocked'; $changed.blockedReason = 'host-unavailable' }
            'counts' { $changed.results.passed = 50 }
            'assurance' { $changed.assurance.poolComplete = $true }
        }
        $changed | ConvertTo-Json -Depth 12 | Set-Content $summaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'stale|inventory|blocked|counters|overclaims'
    }
    Set-Content $summaryPath $summaryText
    (Get-Item $report).LastWriteTimeUtc = $start.AddMinutes(-1)
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'stale'
    Write-Trx $report
    Assert-Rejected { Get-ProofArtifact (Join-Path $root 'settings.json') $RepoRoot 'fixture-receipt' } 'missing|allowlisted'
    if ($null -ne (Get-ProofCommandBinding $RepoRoot 'private' 'private' 'private' 'win-x64')) { throw 'Arbitrary command became inventory evidence.' }
    $checks++
    $noGate = Get-ProofRequiredEnvironment -Command 'runner' -ExecutedCommand 'runner'
    $oneGate = Get-ProofRequiredEnvironment -Command "`$env:OPENCLAW_RUN_E2E = '1'; runner" -ExecutedCommand 'runner'
    if ($noGate.Count -ne 0 -or $oneGate.Count -ne 1 -or $oneGate.OPENCLAW_RUN_E2E -cne '1') {
        throw 'Existing zero-gate or single-gate proof contract changed.'
    }
    $checks++
    Assert-Rejected { Get-ProofRequiredEnvironment -Command "`$env:PRIVATE_VALUE = '1'; runner" -ExecutedCommand 'runner' } 'inventory contract'

    # Child PowerShell supplies a controlled command instead of launching dotnet,
    # exercising the actual runner wiring without builds, apps, or machine changes.
    $harness = Join-Path $root 'runner-harness.ps1'
    $writer = ${function:Write-Trx}.ToString()
    Set-Content $harness @'
param([string]$Repo, [string]$Runner, [string]$Case, [string]$Results)
$ErrorActionPreference = 'Stop'
. (Join-Path $Repo 'scripts\ProofEvidence.ps1')
$sourceProbe = Join-Path $Repo ('scripts\runner-source-probe-' + [guid]::NewGuid().ToString('N') + '.txt')
function global:dotnet {
    if ($Case -in @('unsupported-output', 'lookalike-output', 'missing-e2e-gate', 'missing-ollama-gate')) {
        throw 'Unexpected test execution before prerequisite rejection.'
    }
    $values = @($args)
    $directory = $values[[Array]::IndexOf($values, '--results-directory') + 1]
    $logger = @($values | Where-Object { $_ -like 'trx;LogFileName=*' })[0]
    $report = Join-Path $directory $logger.Substring('trx;LogFileName='.Length)
    $global:LASTEXITCODE = 0
    if ($Case -eq 'missing') { return }
    if ($Case -eq 'malformed') { Set-Content $report '<bad'; return }
    $passed = if ($Case -in @('zero', 'all-skipped')) { 0 } else { 1 }
    $skipped = if ($Case -in @('mixed', 'all-skipped')) { 1 } else { 0 }
    $time = if ($Case -eq 'stale') { [DateTime]::UtcNow.AddDays(-1) } else { [DateTime]::UtcNow }
    Write-Trx $report $passed $skipped 0 $time
    if ($Case -eq 'nonzero') { $global:LASTEXITCODE = 9 }
    if ($Case -eq 'nonzero-source-change') {
        Set-Content -LiteralPath $sourceProbe 'synthetic changed source'
        $global:LASTEXITCODE = 9
    }
}
'@
    Add-Content $harness ("function Write-Trx {`n" + $writer + "`n}")
    Add-Content $harness @'
try {
    if ($Runner -eq 'gateway') {
        $env:OPENCLAW_RUN_E2E = if ($Case -eq 'missing-e2e-gate') { $null } else { '1' }
        $env:OPENCLAW_RUN_OLLAMA_E2E = if ($Case -eq 'missing-ollama-gate') { $null } else { '1' }
        & (Join-Path $Repo 'scripts\run-proof-tests.ps1') -Project 'tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj' -Filter 'FullyQualifiedName~OpenClaw.E2ETests.Setup.SetupAndConnectTests' -ResultName 'gateway-setup-connect' -RuntimeIdentifier win-x64 -ResultsDirectory $Results
    } elseif ($Runner -eq 'adhoc') {
        & (Join-Path $Repo 'scripts\run-proof-tests.ps1') -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=AdHoc' -ResultName 'adhoc-proof' -RuntimeIdentifier win-x64 -ResultsDirectory $Results
        if (@(Get-ChildItem -LiteralPath $Results -Filter 'evidence-summary.json' -Recurse).Count) {
            throw 'Ad hoc command generated inventory evidence.'
        }
    } elseif ($Runner -eq 'proof') {
        & (Join-Path $Repo 'scripts\run-proof-tests.ps1') -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=Accessibility' -ResultName 'winui-accessibility' -RuntimeIdentifier win-x64 -ResultsDirectory $Results
    } else {
        $env:OPENCLAW_CI_COLLECT_COVERAGE = 'false'
        & (Join-Path $Repo 'scripts\Invoke-CiTest.ps1') -Project 'unused.csproj' -ResultsDirectory $Results -TrxFileName 'ci.trx'
    }
    if ($Runner -in @('proof', 'gateway') -and $Case -in @('passed', 'artifact-output')) {
        $summary = @(Get-ChildItem -LiteralPath $Results -Filter 'evidence-summary.json' -Recurse | Sort-Object LastWriteTimeUtc)[-1].FullName
        $checked = Test-ProofEvidence $Repo @($summary) @()
        if (-not $checked.validExecutionEvidence) { throw 'Runner generated invalid evidence.' }
    }
} finally { if (Test-Path -LiteralPath $sourceProbe) { Remove-Item -LiteralPath $sourceProbe } }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'@
    foreach ($runner in @('proof', 'ci', 'gateway', 'adhoc')) {
        $runnerCases = @('passed', 'missing', 'malformed', 'zero', 'all-skipped', 'mixed', 'nonzero', 'stale')
        if ($runner -eq 'proof') { $runnerCases += @('nonzero-source-change', 'artifact-output', 'unsupported-output', 'lookalike-output') }
        if ($runner -eq 'gateway') { $runnerCases = @('passed', 'missing-e2e-gate', 'missing-ollama-gate') }
        if ($runner -eq 'adhoc') { $runnerCases = @('passed') }
        foreach ($case in $runnerCases) {
            $results = switch ($case) {
                'artifact-output' { $artifactRoot }
                'unsupported-output' { $unsupportedRoot }
                'lookalike-output' { $lookalikeRoot }
                default { $root }
            }
            if ($runner -eq 'adhoc') { $results = $unsupportedRoot }
            $savedPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $harness -Repo $RepoRoot -Runner $runner -Case $case -Results $results 2>&1
                $exit = $LASTEXITCODE
            } finally { $ErrorActionPreference = $savedPreference }
            $expectedPass = $case -in @('passed', 'artifact-output') -or ($case -eq 'mixed' -and $runner -eq 'ci')
            if (($exit -eq 0) -ne $expectedPass) { throw "$runner/$case unexpected exit ${exit}: $output" }
            $expectedRejection = switch ($case) {
                'unsupported-output' { 'under TestResults or artifacts' }
                'lookalike-output' { 'under TestResults or artifacts' }
                'missing-e2e-gate' { 'requires OPENCLAW_RUN_E2E=1' }
                'missing-ollama-gate' { 'requires OPENCLAW_RUN_OLLAMA_E2E=1' }
            }
            if ($expectedRejection -and (($output -join "`n") -notmatch $expectedRejection -or
                ($output -join "`n") -match 'Unexpected test execution')) { throw "Prerequisite was not rejected before execution: $output" }
            if ($case -in @('unsupported-output', 'lookalike-output') -and (Test-Path -LiteralPath $results)) {
                throw 'Unsupported output was created before rejection.'
            }
            if ($case -eq 'nonzero-source-change' -and
                (($output -join "`n") -notmatch 'Test command failed with exit code 9' -or
                 ($output -join "`n") -notmatch 'Secondary proof-evidence failure')) {
                throw "Secondary evidence failure replaced the primary command failure: $output"
            }
            $checks++
            $runnerInvocations++
        }
    }
    # Replace only the process-launch boundary in a disposable copy. The real
    # fixture runner still builds ProcessStartInfo and admits the supplied TRX.
    $fixtureRepo = Join-Path $root 'fixture-repo'
    $fixtureScripts = Join-Path $fixtureRepo 'scripts'
    New-Item -ItemType Directory -Path $fixtureScripts | Out-Null
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\TestResultContract.ps1') -Destination $fixtureScripts
    $fixtureSource = Get-Content -LiteralPath (Join-Path $RepoRoot 'scripts\test-gateway-fixture.ps1') -Raw
    $launch = '[Diagnostics.Process]::Start($start)'
    if (($fixtureSource.Split(@($launch), [StringSplitOptions]::None)).Count -ne 2) {
        throw 'Expected exactly one fixture process-launch boundary.'
    }
    Set-Content -LiteralPath (Join-Path $fixtureScripts 'test-gateway-fixture.ps1') -Value $fixtureSource.Replace($launch, '(Start-ControlledFixtureProcess $start)')
    $fixtureHarness = Join-Path $root 'fixture-runner-harness.ps1'
    Set-Content -LiteralPath $fixtureHarness @'
param([string]$Repo, [string]$Case, [int]$FailureIndex, [string]$Configuration, [string]$Results)
$ErrorActionPreference = 'Stop'
$script:calls = 0
$fixtureState = @{ directories = [Collections.Generic.List[string]]::new() }
$app = Join-Path $Repo 'selected app.exe'
Set-Content -LiteralPath $app 'not an executable; never launched'
$env:OPENCLAW_RUN_GATEWAY_FIXTURE_UI = 'parent-gate'
$env:OPENCLAW_GATEWAY_FIXTURE_APP = 'parent-app'
$env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = 'parent-artifacts'
$env:OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS = 'parent-screenshots'
$env:OPENCLAW_REPO_ROOT = 'parent-root'
function Start-ControlledFixtureProcess($Start) {
    $script:calls++
    $index = $script:calls
    $projects = @('OpenClaw.Tray.IntegrationTests', 'OpenClaw.Tray.UITests')
    $filters = @(
        'FullyQualifiedName~GatewayFixtureProfileTests|FullyQualifiedName~GatewayFixtureRunTests|FullyQualifiedName~GatewayFixtureAppTests',
        'FullyQualifiedName~GatewayFixtureUiTests'
    )
    $names = @('fixture-profile-app', 'fixture-ui')
    $arch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    $rid = if ($arch -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    $platform = if ($arch -eq 'Arm64') { 'ARM64' } else { 'x64' }
    $values = @($Start.ArgumentList)
    $directory = $values[[Array]::IndexOf($values, '--results-directory') + 1]
    $runParent = Split-Path -Parent $directory
    if ((Split-Path -Parent $runParent) -cne $Results) {
        throw 'Fixture report escaped the selected artifacts root.'
    }
    if ($fixtureState.directories.Count -gt 0 -and
        $runParent -cne (Split-Path -Parent $fixtureState.directories[0])) {
        throw 'Fixture invocations do not share one run parent.'
    }
    if (-not (Test-Path -LiteralPath $directory -PathType Container) -or
        @(Get-ChildItem -LiteralPath $directory -Force).Count -ne 0 -or
        $directory -in $fixtureState.directories) { throw 'Fixture invocation directory is not fresh and unique.' }
    $fixtureState.directories.Add($directory)
    $expected = @('test', (Join-Path $Repo "tests\$($projects[$index - 1])\$($projects[$index - 1]).csproj"),
        '-c', $Configuration, '-r', $rid, "-p:Platform=$platform", '-p:DevBuild=false',
        '--filter', $filters[$index - 1], '--results-directory', $directory,
        '--logger', "trx;LogFileName=$($names[$index - 1]).trx")
    if (($values | ConvertTo-Json -Compress) -cne ($expected | ConvertTo-Json -Compress) -or
        $Start.FileName -cne 'dotnet' -or $Start.UseShellExecute -or $Start.WorkingDirectory -cne $Repo) {
        throw 'Fixture runner changed its exact project/filter/build/process contract.'
    }
    foreach ($entry in @{
        OPENCLAW_RUN_GATEWAY_FIXTURE_UI = '1'
        OPENCLAW_GATEWAY_FIXTURE_APP = $app
        OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = $Results
        OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS = $(if ($Configuration -eq 'Release') { '1' } else { '0' })
        OPENCLAW_REPO_ROOT = $Repo
    }.GetEnumerator()) {
        if ($Start.Environment[$entry.Key] -cne $entry.Value) { throw "Incorrect child environment: $($entry.Key)" }
    }
    if ($env:OPENCLAW_RUN_GATEWAY_FIXTURE_UI -cne 'parent-gate' -or
        $env:OPENCLAW_GATEWAY_FIXTURE_APP -cne 'parent-app' -or
        $env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS -cne 'parent-artifacts' -or
        $env:OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS -cne 'parent-screenshots' -or
        $env:OPENCLAW_REPO_ROOT -cne 'parent-root') { throw 'Fixture runner leaked environment into its parent.' }
    $activeCase = if ($index -eq $FailureIndex) { $Case } else { 'passed' }
    $report = Join-Path $directory "$($names[$index - 1]).trx"
    if ($activeCase -eq 'malformed') { Set-Content $report '<bad' }
    elseif ($activeCase -ne 'missing') {
        $passed = if ($activeCase -in @('zero', 'all-skipped')) { 0 } else { 2 }
        $skipped = if ($activeCase -in @('mixed', 'all-skipped')) { 1 } else { 0 }
        $failed = if ($activeCase -eq 'failed') { 1 } else { 0 }
        $time = if ($activeCase -eq 'stale') { [DateTime]::UtcNow.AddDays(-1) } else { [DateTime]::UtcNow }
        Write-Trx $report $passed $skipped $failed $time
        if ($activeCase -eq 'stale-file') { (Get-Item $report).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-1) }
        if ($activeCase -eq 'inconsistent') {
            (Get-Content $report -Raw).Replace("outcome='Passed'", "outcome='Failed'") | Set-Content $report
        }
        if ($activeCase -eq 'duplicate') {
            [xml]$trx = Get-Content $report -Raw
            foreach ($leaf in $trx.TestRun.Results.UnitTestResult) { $leaf.executionId = 'duplicate' }
            $trx.Save($report)
        }
    }
    $process = [pscustomobject]@{
        ExitCode = $(if ($activeCase -eq 'nonzero') { 9 } else { 0 })
        Timeout = ($activeCase -eq 'timeout')
        Killed = $false
        Disposed = $false
        Index = $index
    }
    $process | Add-Member ScriptMethod WaitForExit {
        param($Milliseconds)
        if ($Milliseconds -ne 900000) { throw 'Fixture wait deadline changed.' }
        return -not $this.Timeout
    }
    $process | Add-Member ScriptMethod Kill {
        param($EntireTree)
        if (-not $this.Timeout -or -not $EntireTree) { throw 'Unexpected owned-process cleanup.' }
        $this.Killed = $true
    }
    $process | Add-Member ScriptMethod Dispose {
        if ($this.Timeout -ne $this.Killed) { throw 'Timed-out owned process tree was not stopped.' }
        $this.Disposed = $true
        Write-Host "Fixture process disposed: $($this.Index); killed=$($this.Killed)"
    }
    Write-Host "Fixture controlled launch: $index"
    return $process
}
'@
    Add-Content -LiteralPath $fixtureHarness ("function Write-Trx {`n" + $writer + "`n}")
    Add-Content -LiteralPath $fixtureHarness @'
try {
    & (Join-Path $Repo 'scripts\test-gateway-fixture.ps1') -AppPath $app -Configuration $Configuration -ArtifactsDirectory $Results -Screenshots:($Configuration -eq 'Release')
} catch {
    Write-Host $_.ScriptStackTrace
    throw
}
'@
    foreach ($configuration in @('Debug', 'Release')) {
        $fixtureCases = if ($configuration -eq 'Release') { @('passed') } else {
            @('stale', 'passed', 'missing', 'malformed', 'zero', 'all-skipped', 'mixed', 'nonzero', 'stale-file', 'inconsistent', 'duplicate', 'failed', 'timeout')
        }
        foreach ($case in $fixtureCases) {
            $failureIndices = if ($case -eq 'passed') { @(1) } else { @(1, 2) }
            foreach ($failureIndex in $failureIndices) {
                $fixtureResults = Join-Path $root ([guid]::NewGuid().ToString('N'))
                $savedPreference = $ErrorActionPreference
                try {
                    $ErrorActionPreference = 'Continue'
                    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $fixtureHarness -Repo $fixtureRepo -Case $case -FailureIndex $failureIndex -Configuration $configuration -Results $fixtureResults 2>&1
                    $exit = $LASTEXITCODE
                } finally { $ErrorActionPreference = $savedPreference }
                $text = $output -join "`n"
                $expectedPass = $case -eq 'passed'
                if (($exit -eq 0) -ne $expectedPass) { throw "fixture/$configuration/$failureIndex/$case unexpected exit ${exit}: $text" }
                $expectedCalls = if ($expectedPass) { 2 } else { $failureIndex }
                $reports = @(Get-ChildItem -LiteralPath $fixtureResults -Filter '*.trx' -Recurse | Select-Object -ExpandProperty FullName)
                for ($index = 1; $index -le $expectedCalls; $index++) {
                    $name = @('fixture-profile-app', 'fixture-ui')[$index - 1]
                    $diagnostic = [regex]::Match($text, "(?m)^Fixture TRX \($name\): (.+)\r?$")
                    $path = $diagnostic.Groups[1].Value.TrimEnd("`r")
                    if (-not $diagnostic.Success -or
                        [IO.Path]::GetFileName($path) -cne "$name.trx" -or
                        (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $path))) -cne $fixtureResults -or
                        $text.IndexOf($diagnostic.Value, [StringComparison]::Ordinal) -gt
                        $text.IndexOf("Fixture controlled launch: $index", [StringComparison]::Ordinal)) {
                        throw "Fixture did not identify the actual suite/report before execution: $text"
                    }
                    if (($case -ne 'missing' -or $index -ne $failureIndex) -and $path -cnotin $reports) {
                        throw "Fixture diagnostic did not name the actual report: $text"
                    }
                }
                if ([regex]::Matches($text, 'Fixture controlled launch:').Count -ne $expectedCalls -or
                    [regex]::Matches($text, 'Fixture process disposed:').Count -ne $expectedCalls) {
                    throw "Fixture did not stop at the failing invocation or dispose every owned process: $text"
                }
                $rejection = switch ($case) {
                    'missing' { 'missing|no test report' }
                    'malformed' { 'Load|XML|end of file|unexpected' }
                    'zero' { 'zero|did not execute' }
                    'all-skipped' { 'zero|did not execute' }
                    'mixed' { 'Skips' }
                    'nonzero' { 'exit code 9|exit 9' }
                    'stale' { 'stale' }
                    'stale-file' { 'stale' }
                    'inconsistent' { 'counters disagree' }
                    'duplicate' { 'identities' }
                    'failed' { 'failed tests' }
                    'timeout' { 'exceeded 15 minutes' }
                }
                if ($rejection -and $text -notmatch $rejection) { throw "Fixture rejected for the wrong reason (wanted $rejection): $text" }
                if ($expectedPass -and ([regex]::Matches($text, '"executed":2').Count -ne 2 -or
                    [regex]::Matches($text, 'Fixture TRX \(').Count -ne 2)) {
                    throw "Fixture did not report both admitted execution counts and report paths: $text"
                }
                $checks++
                $runnerInvocations++
            }
        }
    }
    $invocationLine = '$invocation = New-TestInvocation -ResultsDirectory $resultsDirectory -TrxFileName "$ResultName.trx"'
    if ($fixtureSource.Split(@($invocationLine), [StringSplitOptions]::None).Count -ne 2) {
        throw 'Expected exactly one fixture invocation boundary for negative controls.'
    }
    foreach ($mutation in @('wrong-root', 'split-parent')) {
        $replacement = if ($mutation -eq 'wrong-root') {
            '$invocation = New-TestInvocation -ResultsDirectory (Join-Path $repoRoot "wrong-root") -TrxFileName "$ResultName.trx"'
        } else {
            '$invocation = New-TestInvocation -ResultsDirectory (Join-Path $artifacts ([guid]::NewGuid().ToString("N"))) -TrxFileName "$ResultName.trx"'
        }
        $mutant = $fixtureSource.Replace($invocationLine, $replacement).Replace($launch, '(Start-ControlledFixtureProcess $start)')
        Set-Content -LiteralPath (Join-Path $fixtureScripts 'test-gateway-fixture.ps1') -Value $mutant
        $fixtureResults = Join-Path $root ([guid]::NewGuid().ToString('N'))
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $fixtureHarness -Repo $fixtureRepo -Case passed -FailureIndex 1 -Configuration Debug -Results $fixtureResults 2>&1
            $exit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $savedPreference }
        $text = $output -join "`n"
        $reason = if ($mutation -eq 'wrong-root') { 'escaped the selected artifacts root' } else { 'do not share one run parent' }
        $expectedCalls = if ($mutation -eq 'wrong-root') { 0 } else { 1 }
        if ($exit -eq 0 -or $text -notmatch $reason -or
            [regex]::Matches($text, 'Fixture controlled launch:').Count -ne $expectedCalls) {
            throw "Fixture path guard did not reject $mutation for the intended reason: $text"
        }
        $checks++
        $runnerInvocations++
    }
    # The real E2E entry point runs in a child shell with only dotnet replaced.
    # Fixed sibling artifacts model earlier attempts and workflow provenance.
    $e2eHarness = Join-Path $root 'e2e-runner-harness.ps1'
    Set-Content -LiteralPath $e2eHarness @'
param([string]$Repo, [string]$Case, [string]$Name, [int]$ProofIndex, [string]$Results)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Set-Location $Results
if ($Case -eq 'psdrive-location') {
    New-PSDrive -Name E2eRepo -PSProvider FileSystem -Root $Results | Out-Null
    Set-Location E2eRepo:\
} elseif ($Case -eq 'provider-location') {
    Set-Location "Microsoft.PowerShell.Core\FileSystem::$Results"
}
if ($Case -in @('location-mismatch', 'psdrive-location', 'provider-location')) {
    $processDirectory = Join-Path $Results 'process-cwd'
    New-Item -ItemType Directory -Path $processDirectory | Out-Null
    [Environment]::CurrentDirectory = $processDirectory
} else {
    [Environment]::CurrentDirectory = $Results
}
$filter = switch ($Name) {
    'setup-connect' { 'FullyQualifiedName~OpenClaw.E2ETests.Setup.SetupAndConnectTests|FullyQualifiedName~OpenClaw.E2ETests.Setup.MxcSetupAndConnectTests|FullyQualifiedName~OpenClaw.E2ETests.Setup.MirroredWslPortLeaseTests|FullyQualifiedName~OpenClaw.E2ETests.Setup.SshOwnershipAdversarialProofTests|FullyQualifiedName~OpenClaw.E2ETests.Setup.TrayExecutableResolutionTests' }
    'revocation-recovery' { 'FullyQualifiedName~OpenClaw.E2ETests.Setup.RevocationAndRecoveryTests' }
    'network-recovery' { 'FullyQualifiedName~OpenClaw.E2ETests.Setup.NetworkRecoveryTests' }
}
$proofNames = @(
    'RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox',
    'RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox',
    'UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing',
    'InitialHandshakeListenerReplacementWithholdsCredentialFrame',
    'InitialNodeHandshakeListenerReplacementWithholdsCredentialFrame'
)
$environment = @{
    OPENCLAW_RUN_E2E = '1'
    OPENCLAW_RUN_MXC_E2E = 'inherited-mxc-gate'
    OPENCLAW_RUN_OLLAMA_E2E = 'inherited-ollama-gate'
    OPENCLAW_E2E_GATEWAY_VERSION = '2026.9.9'
    OPENCLAW_REPO_ROOT = $Results
    OPENCLAW_TRAY_DATA_DIR = (Join-Path $Results 'tray-data')
}
foreach ($entry in $environment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
$artifactRoot = Join-Path $Results 'TestResults\E2E'
New-Item -ItemType Directory -Path $artifactRoot | Out-Null
$oldReport = Join-Path $artifactRoot "OpenClaw.E2ETests.$Name.trx"
$baseline = Join-Path $artifactRoot 'gateway-release.json'
Set-Content -LiteralPath $baseline '{"version":"2026.9.9"}'
$state = @{ calls = 0; directories = [Collections.Generic.List[string]]::new() }
function global:dotnet {
    $state.calls++
    $values = @($args)
    $directory = $values[[Array]::IndexOf($values, '--results-directory') + 1]
    $expected = @('test', 'tests/OpenClaw.E2ETests', '--no-build', '-c', 'Debug', '-r', 'win-x64',
        '--verbosity', 'normal', '--results-directory', $directory,
        '--logger', "trx;LogFileName=OpenClaw.E2ETests.$Name.trx", '--logger', 'console;verbosity=detailed',
        '--blame-hang', '--blame-hang-timeout', '600s', '--blame-hang-dump-type', 'none',
        '--filter', $filter)
    if (($values | ConvertTo-Json -Compress) -cne ($expected | ConvertTo-Json -Compress)) {
        throw 'E2E project/filter/build/logger arguments changed.'
    }
    foreach ($entry in $environment.GetEnumerator()) {
        if ([Environment]::GetEnvironmentVariable($entry.Key) -cne $entry.Value) { throw "E2E changed inherited environment: $($entry.Key)" }
    }
    if ((Split-Path -Parent $directory) -cne $artifactRoot -or
        -not (Test-Path -LiteralPath $directory -PathType Container) -or
        @(Get-ChildItem -LiteralPath $directory -Force).Count -ne 0 -or
        $directory -in $state.directories) { throw 'E2E invocation directory is not fresh, unique, and under the uploaded artifact root.' }
    $state.directories.Add($directory)
    Write-Host "E2E controlled launch: $($state.calls)"
    $global:LASTEXITCODE = if ($Case -eq 'nonzero') { 9 } else { 0 }
    $report = Join-Path $directory "OpenClaw.E2ETests.$Name.trx"
    if ($Case -in @('missing', 'nonzero')) { return }
    if ($Case -eq 'malformed') { Set-Content $report '<bad'; return }
    $passed = if ($Case -in @('zero', 'all-skipped')) { 0 } elseif ($Name -eq 'setup-connect') { 6 } else { 1 }
    $skipped = if ($Case -in @('mixed', 'all-skipped')) { 1 } else { 0 }
    if ($Case -like 'mxc-skip-*' -or $Case -like 'ssh-*') { $passed--; $skipped++ }
    $failed = if ($Case -eq 'failed') { 1 } else { 0 }
    $time = if ($Case -eq 'stale') { [DateTime]::UtcNow.AddDays(-1) } else { [DateTime]::UtcNow }
    Write-Trx $report $passed $skipped $failed $time
    [xml]$trx = Get-Content $report -Raw
    $ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $ns.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
    $rows = @($trx.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
    for ($index = 0; $index -lt $rows.Count; $index++) {
        $testName = if ($Name -eq 'setup-connect' -and $index -lt 5) { $proofNames[$index] } else { "OrdinaryTest$index" }
        $rows[$index].SetAttribute('testName', "OpenClaw.E2ETests.Setup.Fixture.$testName")
    }
    if ($Case -eq 'missing-proof') { $rows[$ProofIndex].SetAttribute('testName', 'UnrelatedPassingTest') }
    if ($Case -like 'mxc-skip-*' -or $Case -like 'ssh-*') {
        $rows[$passed].SetAttribute('outcome', 'Passed')
        $rows[$ProofIndex].SetAttribute('outcome', 'NotExecuted')
        if ($Case -in @('mxc-skip-message', 'mxc-skip-stdout')) {
            $output = $trx.CreateElement('Output', $ns.LookupNamespace('t'))
            if ($Case -eq 'mxc-skip-message') {
                $errorInfo = $trx.CreateElement('ErrorInfo', $ns.LookupNamespace('t'))
                $message = $trx.CreateElement('Message', $ns.LookupNamespace('t'))
                $message.InnerText = 'controlled MXC host unavailable'
                [void]$errorInfo.AppendChild($message)
                [void]$output.AppendChild($errorInfo)
            }
            $stdout = $trx.CreateElement('StdOut', $ns.LookupNamespace('t'))
            $stdout.InnerText = 'controlled MXC stdout reason'
            [void]$output.AppendChild($stdout)
            [void]$rows[$ProofIndex].AppendChild($output)
        }
    }
    if ($Case -eq 'inconsistent') { $trx.TestRun.ResultSummary.Counters.total = '99' }
    if ($Case -eq 'duplicate') { foreach ($row in $rows) { $row.SetAttribute('executionId', 'duplicate') } }
    if ($Case -eq 'ssh-duplicate-skip') {
        $rows[$ProofIndex].SetAttribute('outcome', 'Passed')
        $rows[-1].SetAttribute('testName', $rows[$ProofIndex].GetAttribute('testName'))
        $rows[-1].SetAttribute('outcome', 'NotExecuted')
    }
    if ($Case -eq 'grouped') {
        $container = $trx.SelectSingleNode('/t:TestRun/t:Results', $ns)
        $parent = $trx.CreateElement('UnitTestResult', $ns.LookupNamespace('t'))
        $parent.SetAttribute('testName', 'Grouped tests')
        $parent.SetAttribute('outcome', 'Passed')
        $inner = $trx.CreateElement('InnerResults', $ns.LookupNamespace('t'))
        foreach ($row in $rows) { [void]$inner.AppendChild($row) }
        [void]$parent.AppendChild($inner)
        [void]$container.AppendChild($parent)
    }
    $trx.Save($report)
    if ($Case -eq 'stale-file') { (Get-Item $report).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-1) }
}
'@
    Add-Content -LiteralPath $e2eHarness ("function Write-Trx {`n" + $writer + "`n}")
    Add-Content -LiteralPath $e2eHarness @'
Write-Trx $oldReport 1 0 0 ([DateTime]::UtcNow.AddDays(-1))
$oldHash = (Get-FileHash $oldReport).Hash
try {
    $iterations = if ($Case -eq 'passed') { 2 } else { 1 }
    for ($iteration = 0; $iteration -lt $iterations; $iteration++) {
        & (Join-Path $Repo 'scripts\Invoke-CiE2e.ps1') -Name $Name -Filter $filter
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
} finally {
    if ((Get-FileHash $oldReport).Hash -cne $oldHash -or
        (Get-Content $baseline -Raw).Trim() -cne '{"version":"2026.9.9"}') { throw 'E2E changed sibling report/provenance artifacts.' }
}
'@
    foreach ($name in @('setup-connect', 'revocation-recovery', 'network-recovery')) {
        $e2eCases = @('psdrive-location', 'provider-location', 'location-mismatch', 'passed', 'missing', 'malformed', 'zero', 'all-skipped', 'mixed', 'nonzero', 'stale', 'stale-file', 'inconsistent', 'failed')
        if ($name -eq 'setup-connect') {
            $e2eCases += @('duplicate', 'grouped', 'missing-proof', 'mxc-skip-message', 'mxc-skip-stdout', 'mxc-skip-empty', 'ssh-skip', 'ssh-duplicate-skip')
        }
        foreach ($case in $e2eCases) {
            $proofIndices = switch ($case) {
                'missing-proof' { 0..4 }
                { $_ -like 'mxc-skip-*' } { 0..1 }
                { $_ -like 'ssh-*' } { 2..4 }
                default { 0 }
            }
            foreach ($proofIndex in $proofIndices) {
                $e2eResults = Join-Path $root ([guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $e2eResults | Out-Null
                $savedPreference = $ErrorActionPreference
                try {
                    $ErrorActionPreference = 'Continue'
                    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $e2eHarness -Repo $RepoRoot -Name $name -Case $case -ProofIndex $proofIndex -Results $e2eResults 2>&1
                    $exit = $LASTEXITCODE
                } finally { $ErrorActionPreference = $savedPreference }
                $text = $output -join "`n"
                $expectedPass = $case -in @('psdrive-location', 'provider-location', 'location-mismatch', 'passed', 'mixed', 'grouped') -or $case -like 'mxc-skip-*'
                if (($exit -eq 0) -ne $expectedPass) { throw "e2e/$name/$case/$proofIndex unexpected exit ${exit}: $text" }
                $expectedCalls = if ($case -eq 'passed') { 2 } else { 1 }
                if ([regex]::Matches($text, 'E2E controlled launch:').Count -ne $expectedCalls) { throw "E2E did not execute the expected command: $text" }
                $diagnostics = [regex]::Matches($text, "(?m)^E2E TRX \($name\): (.+)\r?$")
                if ($diagnostics.Count -ne $expectedCalls) { throw "E2E did not identify the shard and report on every path: $text" }
                for ($index = 0; $index -lt $diagnostics.Count; $index++) {
                    $path = $diagnostics[$index].Groups[1].Value.TrimEnd("`r")
                    if ([IO.Path]::GetFileName($path) -cne "OpenClaw.E2ETests.$name.trx" -or
                        (Split-Path -Parent (Split-Path -Parent $path)) -cne (Join-Path $e2eResults 'TestResults\E2E') -or
                        $diagnostics[$index].Index -gt $text.IndexOf("E2E controlled launch: $($index + 1)", [StringComparison]::Ordinal) -or
                        ($case -notin @('missing', 'nonzero') -and -not (Test-Path -LiteralPath $path -PathType Leaf))) {
                        throw "E2E diagnostic does not name the actual invocation report before execution: $text"
                    }
                }
                $rejection = switch ($case) {
                    'missing' { 'missing' }
                    'malformed' { 'Load|XML|end of file|unexpected' }
                    { $_ -in @('zero', 'all-skipped') } { 'zero' }
                    { $_ -in @('stale', 'stale-file') } { 'stale' }
                    'inconsistent' { 'counters disagree' }
                    'duplicate' { 'identities' }
                    'failed' { 'failed tests' }
                    'missing-proof' { 'did not report the (MXC|SSH ownership) proof test' }
                    { $_ -like 'ssh-*' } { 'this proof must pass and may not skip' }
                }
                if ($rejection -and $text -notmatch $rejection) { throw "E2E rejected for the wrong reason (wanted $rejection): $text" }
                if ($case -eq 'nonzero' -and $exit -ne 9) { throw "E2E did not preserve dotnet exit code 9: $text" }
                if ($expectedPass) {
                    $executed = if ($name -eq 'setup-connect') { 6 } else { 1 }
                    $skipped = if ($case -eq 'mixed' -or $case -like 'mxc-skip-*') { 1 } else { 0 }
                    if ($case -like 'mxc-skip-*') { $executed-- }
                    $counts = '"discovered":' + ($executed + $skipped) + ',"executed":' + $executed + ',"passed":' + $executed + ',"failed":0,"skipped":' + $skipped
                    if ([regex]::Matches($text, [regex]::Escape($counts)).Count -ne $expectedCalls) { throw "E2E omitted admitted counts: $text" }
                    if ($name -eq 'setup-connect' -and
                        ([regex]::Matches($text, 'SSH ownership E2E proof passed:').Count -ne 3 * $expectedCalls -or
                         [regex]::Matches($text, 'MXC E2E proof passed:').Count -ne (2 - [int]($case -like 'mxc-skip-*')) * $expectedCalls)) {
                        throw "E2E did not enforce every named proof: $text"
                    }
                    $reason = switch ($case) {
                        'mxc-skip-message' { 'controlled MXC host unavailable' }
                        'mxc-skip-stdout' { 'controlled MXC stdout reason' }
                        'mxc-skip-empty' { 'skip reason was not present in the trx output' }
                    }
                    if ($reason -and $text -notmatch "MXC E2E proof skipped: .+; $reason") { throw "MXC skip lost its reason: $text" }
                } elseif ($text -match 'E2E test results') { throw "Rejected E2E invocation printed admitted results: $text" }
                $checks++
                $runnerInvocations++
            }
        }
    }
    Write-Host "Proof result/evidence regressions passed: $checks checks (including $runnerInvocations controlled runner invocations)."
} finally {
    if (Test-Path $sourceProbe) { Remove-Item -LiteralPath $sourceProbe -Force }
    Remove-Item -LiteralPath $root -Recurse -Force
    if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
    if (Test-Path -LiteralPath $unsupportedRoot) { Remove-Item -LiteralPath $unsupportedRoot -Recurse -Force }
}
$global:LASTEXITCODE = 0
