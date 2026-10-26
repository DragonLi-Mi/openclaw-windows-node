---
name: proof-video
description: "Capture and prepare current-head Windows-node UI screenshots and videos with measured annotations, captions, zoom, and speed cues."
---

# Windows proof video

Use this developer skill to prepare focused visual evidence for OpenClaw Windows
Hub, tray, Command Center, chat, connection and pairing, permissions, packaging,
and visible Windows-node behavior. Use the
[proof and validation skill](../openclaw-proof-validation/SKILL.md) first to
select the required tests, runtime, proof pool, and authority.

This skill covers visual capture and presentation. It does not authorize an app
launch, Gateway registration, install, upgrade, uninstall, permission change,
sensor access, desktop recording, GitHub upload, or PR update. It also does not
turn a screenshot or video into proof of Gateway, MCP, installer, MXC, GPU, ARM64,
or security behavior that the capture did not exercise.

## Choose the claim and supported route

Use the smallest route that can show the changed behavior:

| Surface | Recommended visual source | Boundary |
|---|---|---|
| Windows Hub setup and onboarding | Native UI proof under `OpenClaw.Tray.UITests` with `OPENCLAW_UI_PROOF_DIR`, or an authorized isolated app capture | Native proof requires isolated roots and the reviewed source/build freeze. A fixture does not prove real setup, installation, or pairing. |
| Tray, Settings, and Command Center | Authorized isolated app plus focused app-window capture | Use `windows-winui-interactive`. Do not include unrelated tray icons, notifications, or desktop content. |
| Chat and session UI | Fixture Gateway smoke with `-Screenshots` for deterministic populated states, or an authorized isolated app | Fixture content is synthetic. It does not prove provider output, live Gateway pairing, microphone, notifications, or node execution. |
| Pairing and node status | Real Gateway E2E for the state transition, plus a focused UI capture when visible presentation changed | A static status screenshot alone does not prove bootstrap, operator pairing, node pairing, reconnect, or invocation. |
| Permissions | Isolated profile with reviewed non-secret settings; capture the editor state without invoking sensitive capabilities when possible | A permission toggle screenshot does not prove effective Gateway allowlists or OS permission behavior. |
| Updater, installer, repair, migration, and uninstall | `windows-clean-installer-upgrade`, `windows-11-sac-on`, Store migration guidance, or another declared packaging pool on a disposable/restorable host | Local development builds and fixture captures are not official installer, Store, signature, reputation, upgrade, or uninstall proof. |
| Native capabilities | `winnode`, raw MCP, or real Gateway invocation for the command, with focused UI evidence only for visible results | Do not use the product `screen.record` command as the proof recorder. It is a privacy-sensitive capability with its own consent and allowlist contract. |

Use [proof pools](../../../docs/PROOF_POOLS.md) and
[test selection](../../../docs/TEST_COVERAGE.md#formal-validation-paths) for the
authoritative route. Keep unsupported or unavailable proof as
`Not verified / blocked`.

## Create an evidence directory and bind it to source

Store uncommitted evidence under `artifacts` or `TestResults`. Never commit raw
or polished media. Use a fresh directory after source, layout, crop, or cue
changes so old annotations cannot be mistaken for current evidence.

```powershell
$slug = 'short-proof-name'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw 'Run proof capture from an OpenClaw Windows repository worktree.'
}
$proof = Join-Path $repoRoot "artifacts\proof-video\$slug-$stamp"
New-Item -ItemType Directory -Force $proof | Out-Null

. (Join-Path $repoRoot 'scripts\ProofEvidence.ps1')
$sourceBefore = Get-ProofSourceIdentity -RepoRoot $repoRoot
[ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    source = $sourceBefore
} | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $proof 'source.json') -Encoding UTF8
```

`Get-ProofSourceIdentity` records the exact HEAD plus a digest and count for
relevant changed and untracked source without persisting source paths or file
contents. After capture, and again immediately before final hashing, compare a
fresh identity with `$sourceBefore`:

```powershell
$sourceAfter = Get-ProofSourceIdentity -RepoRoot $repoRoot
if (($sourceAfter | ConvertTo-Json -Compress) -cne
    ($sourceBefore | ConvertTo-Json -Compress)) {
    throw 'Source changed during capture. Discard this stale evidence.'
}
```

For inventory-bound proof, use `scripts\run-proof-tests.ps1` and its
`evidence-summary.json` contract instead of inventing a second execution
receipt. For native onboarding proof, retain the existing
`OPENCLAW_UI_PROOF_FREEZE_DIR` and `OPENCLAW_UI_PROOF_MANIFEST_SHA256`
source/build freeze. A Git commit alone is not loaded-binary proof.

If source or the tested binary changes while recording or editing, mark the
evidence stale and capture again.

## Capture the raw evidence

Prefer repository-owned PNG capture when a native proof or fixture test already
owns the scenario:

```powershell
$env:OPENCLAW_REPO_ROOT = $repoRoot
.\build.ps1
$rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
    [Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
$app = ".\src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\$rid\OpenClaw.Tray.WinUI.exe"
.\scripts\test-gateway-fixture.ps1 `
  -AppPath $app `
  -ArtifactsDirectory $proof `
  -Screenshots
```

The fixture capture uses the owned app HWND, records adjacent metadata and image
SHA-256, rejects blank or near-uniform content, and rejects cropped controls
when the test supplies asserted elements. Its per-run `run.json` records the
actual app path, hash, assembly hash, and version. Inspect the PNGs before making
a visual claim because generic captures still require visual crop review. UI
Automation assertions or `app.chat.snapshot` are supporting evidence, not pixel
inspection.

For a manual video, use an already available Windows app-window or region
recorder, such as the Windows 11 Snipping Tool screen recorder, only after the
capture is authorized. Record the smallest stable rectangle containing the
OpenClaw window. Keep notifications, taskbar content, other windows, user names,
machine names, profile paths, session titles, authored chat, endpoints, tokens,
setup codes, device identities, API keys, secret-entry fields, and other private
content outside the frame. The selected proof pool's `redact` and
`neverCollect` lists are hard boundaries. Use fixture or disposable test
credentials. Stop recording before entering any secret and resume only after
the field and derived previews are hidden. Disable microphone and system audio
unless audio is the reviewed proof claim and its capture is authorized.

Do not install or add ffmpeg, OBS, an editor, a codec, or another capture
dependency for this skill. The repository currently has no owned renderer that
burns captions, annotations, zoom, and speed cues into a native Windows MP4. If
no editor already installed and authorized for this task is available, retain
the raw capture and cue sidecar, attach a focused still when useful, and report
polished video rendering blocked.

Keep the untouched raw PNG or MP4. Never overwrite it during cropping, markup,
captioning, or transcoding.

## Measure before annotating

Read [measured annotations](references/annotations.md) before adding arrows,
outlines, alignment guides, or spacing brackets.

Use measurements from the settled asserted state:

- Prefer the adjacent JSON written by `OwnedWindowCapture` or
  `OnboardingNativeProof`.
- Otherwise record UI Automation or XAML bounds for the exact owned window.
- Use raw image pixels for native captures. Save DPI and rasterization scale
  when the source reports them.
- Keep the first rectangle as the alignment reference.
- Re-measure after navigation, scrolling, resize, DPI change, theme change, or
  any layout update.
- Clear marks during movement. An annotation is a stationary cue, not an
  element tracker.

Keep marks sparse and labels in empty space. Preserve the measurement sidecar
beside the raw and annotated output.

## Write captions, zoom, and speed cues

Captions are visual narration, not synthesized audio. Keep each caption to one
or two short lines and burn it into the final MP4 because GitHub playback cannot
be assumed to display a separate subtitle track.

Use a sidecar to preserve editorial intent even when the authorized editor is
manual:

```json
{
  "format": "openclaw-windows-proof-cues",
  "version": 1,
  "video": "raw.mp4",
  "rawSha256": "<sha256>",
  "sourceIdentity": {
    "head": "0123456789abcdef0123456789abcdef01234567",
    "dirtySha256": "<sha256>",
    "dirtyFileCount": 4
  },
  "coordinateSpace": "raw-px",
  "frame": { "width": 1440, "height": 900 },
  "cues": [
    {
      "kind": "caption",
      "start": 1.0,
      "end": 4.0,
      "text": "Open the isolated Command Center."
    },
    {
      "kind": "zoom",
      "start": 4.0,
      "end": 7.0,
      "rect": { "x": 820, "y": 120, "width": 420, "height": 260 },
      "factor": 1.8,
      "easeSeconds": 0.4
    },
    {
      "kind": "speed",
      "start": 8.0,
      "end": 14.0,
      "factor": 4
    }
  ]
}
```

All cue times use the raw video clock. Zoom rectangles use the raw captured
frame before editorial scaling or cropping. Speed cues must not overlap other
speed cues, and zoom cues must not overlap other zoom cues. Adjacent cues are
allowed. Do not assume a native recorder produced constant-frame-rate video. If
an authorized editor requires constant frame rate for frame-indexed timing,
retain the original and create a separate working copy, then recheck every cue
boundary against the final output.

Use speed changes only to compress waiting or repeated navigation. Restore
normal speed before the asserted result and hold the final state long enough to
read. Use zoom only to clarify a stable region. Annotations must share the
captured image's crop and zoom transform so they remain attached to the target.
Captions remain fixed in the final viewport.

## Inspect and reject weak evidence

Inspect the entire raw capture and the entire polished output. Also inspect
frames at each caption, annotation, zoom, speed, crop, and cut boundary.

Reject the evidence when any of these are true:

- The frame is blank, near-uniform, empty, clipped, cropped, obscured, or from
  the wrong window.
- The asserted control or changed pixels are outside the frame.
- An annotation points to a moved element or covers the changed state.
- A caption is clipped, unreadable, stale, or claims more than the capture
  shows.
- A zoom hides required context or makes the image too soft to inspect.
- A speed segment skips the state transition or makes the result ambiguous.
- The raw source, final output, cue sidecar, source identity, or loaded binary
  no longer correspond.
- The capture contains private, unrelated, or prohibited material.
- The evidence comes from another commit or an old dirty tree and is presented
  as current-head proof.

Record final hashes without modifying the files:

```powershell
$proofRoot = [IO.Path]::GetFullPath($proof).TrimEnd('\')
$manifest = Join-Path $proofRoot 'sha256.json'
Get-ChildItem -LiteralPath $proofRoot -File -Recurse |
  Where-Object FullName -ne $manifest |
  Get-FileHash -Algorithm SHA256 |
  Sort-Object Path |
  ForEach-Object {
      [pscustomobject][ordered]@{
          file = $_.Path.Substring($proofRoot.Length).TrimStart('\').Replace('\', '/')
          sha256 = $_.Hash
      }
  } |
  ConvertTo-Json -Depth 3 |
  Set-Content -LiteralPath $manifest -Encoding UTF8
```

Hash only reviewed evidence. Do not copy settings, Gateway records, identity
files, credentials, arbitrary logs, or environment dumps into the proof
directory. Review metadata and sidecars for private values before publication;
the relative-path hash manifest is not itself publication authority.

## Prepare PR evidence

Follow [Windows PR preparation](../windows-pr-prepare/SKILL.md). Under
`## Real Behavior Proof`, identify:

- exact HEAD and whether the tree had relevant uncommitted changes;
- app path/hash/version or the native source/build freeze when available;
- runtime and proof pool;
- exact reproduction steps;
- raw and polished artifact hashes;
- what the video directly proves;
- fixture, Gateway, installer, capability, or packaging limits;
- redactions applied;
- whether every linked screenshot or video was opened and verified.

Uploading media or updating a PR requires separate authority. A successful
capture is local evidence until publication is explicitly authorized.

## Source and maintenance

Windows-owned adaptation of the MIT-licensed upstream
[proof-video skill](https://github.com/openclaw/openclaw/blob/0b26ef66c88da33daf15cac50ddad1c2ec8d2173/.agents/skills/proof-video/SKILL.md).
This is not a synchronized copy. Windows owns its capture tools, runtime and
proof-pool boundaries, privacy rules, source/build binding, and publication
authority.
