<#
.SYNOPSIS
    Runs a CI test project with optional push/tag coverage collection.

.DESCRIPTION
    Admits fresh results from a unique invocation directory. Ordinary CI allows
    reported skips alongside executed passes, not complete native/custom proof.
    DevBuild recompiles linked identity code without restoring. SkipCoverage
    retains the uninstrumented accessibility and DevBuild exceptions.
    SummaryTitle opts into a GitHub step summary based only on admitted results.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$TrxFileName,
    [string]$Runtime,
    [string]$Filter,
    [ValidateRange(0, 3600)][int]$HangTimeoutSeconds = 0,
    [switch]$DevBuild,
    [switch]$SkipCoverage,
    [string]$SummaryTitle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "TestResultContract.ps1")
$ResultsDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
$invocation = New-TestInvocation -ResultsDirectory $ResultsDirectory -TrxFileName $TrxFileName
$ResultsDirectory = $invocation.directory
Write-Host "CI TRX: $($invocation.report)"

$testArguments = [System.Collections.Generic.List[string]]::new()
@(
    "test",
    $Project,
    "-c",
    "Debug"
) | ForEach-Object { $testArguments.Add($_) }

if ($DevBuild) {
    $testArguments.Add("--no-restore")
    $testArguments.Add("-p:DevBuild=true")
} else {
    $testArguments.Add("--no-build")
}

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $testArguments.Add("-r")
    $testArguments.Add($Runtime)
}

@(
    "--verbosity",
    "normal",
    "--results-directory",
    $ResultsDirectory,
    "--logger",
    "trx;LogFileName=$TrxFileName"
) | ForEach-Object { $testArguments.Add($_) }

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testArguments.Add("--filter")
    $testArguments.Add($Filter)
}

if ($HangTimeoutSeconds -gt 0) {
    # Keep the interrupted test sequence, not a potentially secret-bearing memory dump.
    @(
        "--blame-hang",
        "--blame-hang-timeout",
        "${HangTimeoutSeconds}s",
        "--blame-hang-dump-type",
        "none"
    ) | ForEach-Object { $testArguments.Add($_) }
}

$result = $null
try {
    $collectCoverage = $env:OPENCLAW_CI_COLLECT_COVERAGE -eq "true" -and -not $SkipCoverage
    if ($collectCoverage) {
        $coverageOutput = Join-Path $ResultsDirectory "coverage.cobertura.xml"
        & dotnet-coverage collect `
            --output $coverageOutput `
            --output-format cobertura `
            dotnet @testArguments
    } else {
        & dotnet @testArguments
    }

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    $result = Read-TestResultContract -Path $invocation.report -StartedUtc $invocation.startedUtc -ExitCode 0 -AllowSkipped
    Write-Host ("CI test results: " + ($result | ConvertTo-Json -Compress))
} finally {
    if (-not [string]::IsNullOrWhiteSpace($SummaryTitle) -and $env:GITHUB_STEP_SUMMARY) {
        $summary = @("### $SummaryTitle", "")
        if ($null -eq $result) {
            $summary += "Test run failed or its report was not admitted. No passing result is established. See the step log and uploaded test results."
        } else {
            $summary += @(
                "Outcome: $($result.outcome).",
                "",
                "| Discovered | Executed | Passed | Failed | Skipped |",
                "| --- | --- | --- | --- | --- |",
                "| $($result.discovered) | $($result.executed) | $($result.passed) | $($result.failed) | $($result.skipped) |",
                "",
                "Counts cover only selected tests in this invocation. Ordinary CI permits reported skips; skipped tests were not validated."
            )
        }
        $summary += @(
            "",
            "This CI result is not complete native/custom proof.",
            "TRX identifier: $($invocation.id)/$TrxFileName",
            ""
        )
        $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
}
# Ordinary CI retains environment-gated skips. This is not native/custom proof.
$global:LASTEXITCODE = 0
