# Accessibility validation

OpenClaw Companion treats accessibility as a CI quality gate for the WinUI app. The Build and Test workflow runs real-process Axe.Windows scans against the tray UI test project after the normal native UI tests.

## What CI enforces

The `.github\workflows\ci.yml` **Run Accessibility Tests (Axe.Windows)** step runs:

```powershell
.\scripts\Invoke-CiTest.ps1 `
    -Project tests\OpenClaw.Tray.UITests `
    -Runtime win-x64 `
    -SkipCoverage `
    -ResultsDirectory TestResults\Accessibility `
    -TrxFileName Accessibility.trx `
    -Filter Category=Accessibility `
    -SummaryTitle "Accessibility (Axe.Windows)"
```

The helper uses prebuilt Debug/win-x64 tests. Those tests launch the app in a real Windows desktop process and scan pages for WCAG violations through Axe.Windows. The GitHub Actions step summary reports admitted selected-test counts and status, with permitted skips explicitly unvalidated. A test-command failure or rejected report produces a generic failure summary, not detailed WCAG findings or a passing result. The summary is not complete native/custom proof.

For failure details, inspect the step log and the `test-results-ui` artifact. When produced, the report is written to `TestResults\Accessibility\<invocation-id>\Accessibility.trx` in the workspace and uploaded as `Accessibility/<invocation-id>/Accessibility.trx`. The helper logs the expected report path before running tests. A skipped step or failure before report creation may leave no TRX, and a rejected report may be unusable.

## Running locally

From the repository root:

```powershell
dotnet build tests\OpenClaw.Tray.UITests -c Debug -r win-x64
dotnet test tests\OpenClaw.Tray.UITests -c Debug -r win-x64 --no-build --filter Category=Accessibility
```

Run this lane for UI changes that add or modify pages, dialogs, controls, focus behavior, labels, contrast-sensitive visuals, or keyboard navigation.

## Relationship to other validation

Accessibility scans do not replace the required agent closeout validation in `AGENTS.md`. They are an additional focused lane for WinUI accessibility work and a formal CI gate documented in `docs\TEST_COVERAGE.md`.
