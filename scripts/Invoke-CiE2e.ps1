<#
.SYNOPSIS
    Runs one existing CI E2E shard and enforces its TRX proof contract.

.DESCRIPTION
    Admits only fresh, consistent reports from this invocation under
    TestResults\E2E\<invocation-id>. Ordinary CI permits reported MXC skips;
    the named SSH ownership proofs must pass. This is not formal MXC proof.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("setup-connect", "revocation-recovery", "network-recovery")]
    [string]$Name,
    [Parameter(Mandatory)][string]$Filter,
    [ValidateRange(1, 3600)][int]$HangTimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "TestResultContract.ps1")

$resultsDirectory = Join-Path (Get-Location).ProviderPath "TestResults\E2E"
$invocation = New-TestInvocation -ResultsDirectory $resultsDirectory -TrxFileName "OpenClaw.E2ETests.$Name.trx"
$resultsDirectory = $invocation.directory
Write-Host "E2E TRX ($Name): $($invocation.report)"
# VSTest's hang timer resets on test events, not fixture progress. A progressing
# WSL install can exceed it before the first case starts. Bound output inactivity
# instead; the existing workflow step still caps total wall time at 20 minutes.
$testArguments = @(
    'test', 'tests/OpenClaw.E2ETests', '--no-build', '-c', 'Debug', '-r', 'win-x64',
    '--verbosity', 'normal', '--results-directory', $resultsDirectory,
    '--logger', "trx;LogFileName=OpenClaw.E2ETests.$Name.trx",
    '--logger', 'console;verbosity=detailed', '--blame', '--filter', $Filter
)
& (Join-Path $PSScriptRoot 'Invoke-CiProcess.ps1') -FilePath 'dotnet' `
    -ArgumentList $testArguments -IdleTimeoutSeconds $HangTimeoutSeconds `
    -OutputPath (Join-Path $resultsDirectory "OpenClaw.E2ETests.$Name.console.log")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$result = Read-TestResultContract -Path $invocation.report -StartedUtc $invocation.startedUtc -ExitCode 0 -AllowSkipped
if ($Name -ne "setup-connect") {
    Write-Host "E2E test results ($Name): $($result | ConvertTo-Json -Compress)"
    return
}

[xml]$trx = Get-Content -LiteralPath $invocation.report -Raw
$ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
$ns.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
$proofResults = @($trx.SelectNodes('/t:TestRun/t:Results//t:UnitTestResult[not(t:InnerResults)]', $ns))
$mxcProofNames = @(
    "RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox",
    "RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox"
)
foreach ($mxcProofName in $mxcProofNames) {
    $mxcProofs = @(
        $proofResults |
            Where-Object { $_.testName -like "*$mxcProofName*" }
    )
    if ($mxcProofs.Count -eq 0) {
        throw "E2E shard '$Name' did not report the MXC proof test '$mxcProofName'. Check the setup-connect filter before merging."
    }

    foreach ($mxcProof in $mxcProofs) {
        $mxcOutcome = [string]$mxcProof.outcome
        if ($mxcOutcome -eq "Passed") {
            Write-Host "MXC E2E proof passed: $mxcProofName"
        } elseif ($mxcOutcome -eq "NotExecuted") {
            $mxcSkipReason = @(
                $mxcProof.SelectSingleNode("t:Output/t:ErrorInfo/t:Message", $ns)
                $mxcProof.SelectSingleNode("t:Output/t:StdOut", $ns)
            ) |
                Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace($_.InnerText) } |
                ForEach-Object { $_.InnerText.Trim() } |
                Select-Object -First 1
            if ([string]::IsNullOrWhiteSpace($mxcSkipReason)) {
                $mxcSkipReason = "skip reason was not present in the trx output"
            }
            Write-Warning "MXC E2E proof skipped: $mxcProofName; $mxcSkipReason"
        } else {
            throw "MXC E2E proof '$mxcProofName' had unexpected outcome '$mxcOutcome'."
        }
    }
}

$sshOwnershipProofNames = @(
    "UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing",
    "InitialHandshakeListenerReplacementWithholdsCredentialFrame",
    "InitialNodeHandshakeListenerReplacementWithholdsCredentialFrame"
)
foreach ($sshOwnershipProofName in $sshOwnershipProofNames) {
    $sshOwnershipProofs = @(
        $proofResults |
            Where-Object { $_.testName -like "*$sshOwnershipProofName*" }
    )
    if ($sshOwnershipProofs.Count -eq 0) {
        throw "E2E shard '$Name' did not report the SSH ownership proof test '$sshOwnershipProofName'. Check the setup-connect filter before merging."
    }

    foreach ($sshOwnershipProof in $sshOwnershipProofs) {
        $sshOwnershipOutcome = [string]$sshOwnershipProof.outcome
        if ($sshOwnershipOutcome -ne "Passed") {
            throw "SSH ownership E2E proof '$sshOwnershipProofName' had outcome '$sshOwnershipOutcome'; this proof must pass and may not skip."
        }
        Write-Host "SSH ownership E2E proof passed: $sshOwnershipProofName"
    }
}
Write-Host "E2E test results ($Name): $($result | ConvertTo-Json -Compress)"
