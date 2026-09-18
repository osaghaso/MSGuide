# MSGuide: Teams camera and voice walkthrough

This runbook covers the live test and presentation scenario: **ask by text or
voice, inspect the current state, approve the necessary change, and verify the
result**. MSGuide is an independent Hackathon project, separate from ExP.

The normal interface is question-first. It does not show developer demos or
describe the product as camera-only. The implemented camera workflow still has
explicit limits; do not present it as unrestricted desktop automation.

## Prerequisites

- An unlocked Windows 11 desktop. The live scenario was exercised on an ARM64
  Snapdragon PC with English-language Teams.
- PowerShell 7, .NET SDK 10.0.303 (or the patch allowed by `global.json`),
  and Windows x64 CPython 3.11.9. Use x64 Python on ARM64 Windows for these locks.
- A working camera and a visible **Teams meeting or prejoin** window.
- For voice: a microphone and the local Whisper English model.
- For `-Copilot`: an installed, authenticated Copilot CLI and access to the
  configured model. No Agency installation is required.

Use a test meeting/prejoin, not an active meeting that would be disrupted by
permission changes or a restart. Keep unrelated confidential windows out of
screen sharing.

## One-time setup

Run from the repository root in PowerShell 7:

```powershell
python -m venv venv
.\venv\Scripts\python -m pip install --require-hashes --only-binary=:all: -r requirements-dev.lock.txt
dotnet restore .\desktop\MSGuide.Desktop.csproj --locked-mode

# Run only after approving the approximately 466 MiB model download.
.\scripts\Install-MSGuideSpeechModel.ps1 -AcceptDownload
```

`requirements-dev.lock.txt` contains the tested runtime/test closure and wheel
hashes without workstation-specific mirror URLs. Runtime-only installations use
`requirements.lock.txt` with the same hash/binary flags. Normal installation does
not need experimental lock support. Direct intent remains in `requirements.txt` /
`requirements-dev.txt`; regenerate locks intentionally after manifest changes with
pip 26.2.1 through `scripts\lock_python_dependencies.py`, without weakening TLS or
incorporating unrelated packages from an existing venv.

The model installer verifies its size and SHA-256 digest, then stores
`ggml-small.en.bin` under `%LOCALAPPDATA%\MSGuide\models`. It does not record or
upload audio. Normal application startup never downloads a model. An existing
model with a mismatched digest is not overwritten automatically.

No Developer Mode, MSIX registration, Azure speech subscription, or cloud audio
processing is required.

## Start the live experience

```powershell
.\scripts\Start-MSGuide.ps1 -Port 8778 -Copilot -Shareable
```

- **Do not add `-CameraFixture` for live camera acceptance.** It simulates the
  action and cannot fix the real camera.
- Leave `-DeveloperTools` off for the normal presentation.
- `-Shareable` allows the companion and overlay to appear in full-screen
  sharing. Without it, they are excluded by default.
- The launcher owns the desktop and backend lifetime. Keep its terminal open.
- The URL is a loopback API, not a browser application. Its health route is:

```powershell
Invoke-RestMethod http://127.0.0.1:8778/health
```

Port 8765 is the default when `-Port` is omitted. If the requested port is busy,
close the existing owned instance normally or choose another port; the launcher
does not kill unrelated processes.

`-Copilot` configures the SDK for **GPT-6 Astra**, `xhigh` reasoning and
`long_context`, subject to model entitlement. The SDK supplies reviewed
screen-context guidance. **Camera diagnosis/actions and Whisper transcription
remain local**; the camera state machine is not driven by an unrestricted model.
See [provider setup](../MODEL_SETUP.md) for overrides and data handling.

## Primary case: Teams camera is off

Prepare the Teams prejoin with its camera toggle **off**, while Windows
device-wide camera access, app camera access, and Teams permission are **on**.

1. In MSGuide, select **Fix it for me**.
2. Type **My camera isn't working in Teams** and select **Ask MSGuide**, or
   press Enter. Typing alone must not start recovery; Shift+Enter adds a line.
3. If asked, select the exact meeting/prejoin window. Selection triggers
   read-only inspection automatically.
4. Review **Approve & turn camera on**. Asking the question did not authorize
   this action.
5. Approve once. MSGuide reacquires and validates the observed UI Automation
   control, invokes it once, and inspects the result.
6. Confirm the real Teams preview appears. MSGuide should show **Camera ready**
   only after Teams reports camera on and Windows reports active camera use.

An enabled camera button alone is not readiness. If the camera fails, the app
must describe the blocker rather than claim success or automatically retry.

## Permission-block case

This branch exercises the failure discovered during live testing: **Windows
device-wide Camera access can be off even when Teams' stored permission says
Allow**.

1. In Windows **Settings > Privacy & security > Camera**, turn **Camera access**
   off yourself. This affects other apps, so do it only in an appropriate test
   environment.
2. Ask MSGuide to fix the Teams camera.
3. Expect a device-wide permission diagnosis, not an immediate camera-on offer.
   Disabled child toggles must not be mistaken for policy-managed controls when
   the parent switch is off.
4. Select **Open Camera settings**, then inspect the actual Camera privacy page.
5. Review **Approve & enable Camera access**. Its disclosure must explain that
   this change affects other apps that already have camera permission.
6. Approve that scope only. If app-level or Teams-level permission is also off,
   each next scope requires a separate approval.
7. After permissions are on, MSGuide returns to fresh Teams inspection.
   **Turning on the Teams camera requires its own approval.**
8. If a Teams restart is necessary, it requires a further explicit approval;
   it can end an active meeting. Reopening the camera surface manually is an
   alternative.

Restore any deliberately changed test settings afterward. MSGuide does not
override policy, silently change unrelated settings, or automatically restart
Teams.

## Guide me and already-working cases

| Case | Expected behavior |
| --- | --- |
| **Guide me**, camera off | MSGuide identifies and shows the verified control; the user changes it and requests another check. No automatic control action. |
| Camera already working | A fresh question recognizes camera-on plus current Windows active-use evidence and reports **Already working**. No new off-to-on transition is required. |
| **Start over** after success | A fresh, read-only reassessment recognizes the current state. It does not toggle the camera, reuse approval, or create a false failure. |
| **Stop recovery** | Pending recovery is cancelled and modes become selectable. |
| **Stop & switch** beside locked modes | The old run and approval are cleared before the other mode is selected. Completed changes are not undone. |
| Edit the question | Old work/approval is cancelled and task-specific UI resets for the new draft. |

## Voice case

1. Select **Voice settings**, choose the intended microphone, then return to the
   question. The live test used the built-in Qualcomm array; a Scarlett input
   was also available. Selecting an input in MSGuide does not change Windows'
   default input.
2. Select **Start microphone** and say **Check my Teams camera**.
3. Watch the input meter. For a low signal, check the selected device, distance,
   volume, and hardware mute rather than assuming the recognizer is working.
4. Select **Stop & transcribe**. The microphone closes before local Whisper
   inference. Recording is capped at 30 seconds; transcription has a 45-second
   deadline and normally takes several seconds on the tested machine.
5. Review and correct the resulting text. Recognition may use forms such as
   **team's**; MSGuide preserves the words rather than rewriting them to a
   scripted expected phrase. Non-speech `[BLANK_AUDIO]` annotations are omitted.
6. Select **Ask MSGuide**. Speech must never submit the request automatically.

Repeat-recording behavior:

| Action | Expected behavior |
| --- | --- |
| **New voice question** | The previous draft stays until new speech is successfully recognized, then is replaced. |
| **Add more** | New speech is explicitly appended to the existing question. |
| Cancel or fail a replacement | The existing draft is retained. |
| Type while recording/transcribing | Pending speech is cancelled so late results cannot overwrite the edits. |
| **Cancel transcription** | Local processing is cancelled; no question is submitted. |
| **MIC STATUS UNKNOWN** | A driver stop was not confirmed. Close MSGuide before trying another recording; the app must not pretend the microphone is off. |

Audio is bounded in memory, not saved or uploaded. Confidence warnings still
require human review; successful recognition is not permission to act.

## Product-shell checks

- Startup shows **Your guide to getting things done at Microsoft**, a blank
  question, and the two interaction modes.
- There is no normal **Advanced** section, synthetic task launcher, or demo
  framing.
- **Settings** contains voice, connection, and privacy controls.
- **Check connection** must not cancel or overwrite a completed camera result.
- At a small window size, the workspace can scroll to the question, task
  actions, Settings, and approvals.
- An unsupported automated request explains the limitation and offers
  **Use screen context**. Opening it does **not** capture or upload anything.
- Screen guidance has separate **choose window > capture locally > review >
  approve sharing** steps. Shared context is limited to approved public or
  synthetic content; do not use confidential screen contents for this test.

## Suggested short presentation

| Step | Show |
| --- | --- |
| Ask | Blank MSGuide question, **Fix it for me**, and a real Teams prejoin with camera off. |
| Inspect | Type the camera question and choose the correct window if needed. |
| Approve | The specific camera-on approval; or the separately scoped Windows permission approval if demonstrating that branch. |
| Verify | The real preview and **Camera ready**, not merely a green scripted indicator. |
| Reassess | **Start over** reports **Already working** without making another change. |
| Voice | Speak a new question, stop, review its local transcript, then ask. |

Avoid changing permission state midway through an unrelated meeting or showing
private desktop content. A simulated fixture is useful for rehearsal, but is
not evidence of live recovery.

## Regression commands

The following uses the existing test runners. No real microphone is opened by
the automated speech tests.

```powershell
.\venv\Scripts\python.exe -m pytest -q `
    tests\test_copilot_provider.py tests\test_camera_recovery.py tests\test_api.py
```

To validate the desktop without overwriting the binary of a running instance:

```powershell
$out = Join-Path $env:TEMP ("MSGuide-validation-" + [guid]::NewGuid().ToString("N"))
dotnet build .\desktop\MSGuide.Desktop.csproj --nologo -warnaserror --output $out
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed." }

$previousCamera = $env:MSGUIDE_CAMERA_RECOVERY_TESTS
$previousWhisper = $env:MSGUIDE_WHISPER_SYNTHETIC_TEST
try {
    $env:MSGUIDE_CAMERA_RECOVERY_TESTS = "1"
    $env:MSGUIDE_WHISPER_SYNTHETIC_TEST = "1"
    $report = Join-Path $out "self-test.json"
    $arguments = '--self-test --test-results "{0}"' -f $report
    $test = Start-Process -FilePath (Join-Path $out "MSGuide.Desktop.exe") `
        -ArgumentList $arguments -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw "Desktop self-tests failed; see $report" }
    Get-Content -LiteralPath $report
} finally {
    $env:MSGUIDE_CAMERA_RECOVERY_TESTS = $previousCamera
    $env:MSGUIDE_WHISPER_SYNTHETIC_TEST = $previousWhisper
}
```

The optional Whisper synthetic check requires the installed model and a Windows
TTS voice. It generates public test speech in memory; it does not use the
microphone or speakers. Keep the report if needed, then remove the generated
`$out` directory.

Automated coverage includes permission precedence, separate approval scopes,
current-readiness reassessment, mode switching, draft submission, developer
visibility, voice replacement/append, cancellation, bounded PCM capture, silence,
and local model inference. It does not replace the real hardware cases above.

For an explicitly simulated UI rehearsal:

```powershell
.\scripts\Start-MSGuide.ps1 -Port 8779 -CameraFixture -Shareable
```

Expect **Fixture complete**, never real **Camera ready**. For developer-only
Build Center/Notepad and manual context diagnostics, add `-DeveloperTools`.

## Known limits and troubleshooting

| Symptom | Check |
| --- | --- |
| The real camera did not change, but the fixture completed | Relaunch without `-CameraFixture`. |
| Teams says camera failed | Check device-wide, user-app, and Teams permission levels; an app-level Allow does not override a device-wide Deny. |
| Controls are missing or ambiguous | Use the exact visible Teams window. Unsupported, stale, incomplete, or duplicate controls fail closed. |
| Another language/localized control name | The camera matcher currently supports specific English names, including `Turn camera on (Ctrl+Shift+O)`. Other locales are not accepted implicitly. |
| Permission is on but readiness is unresolved | Camera-on and active use still need verification. Reopen the camera surface or separately approve a restart if appropriate. |
| Voice is inaccurate | Confirm the input and signal level, then edit the transcript. Do not treat a generated phrase as an approved command. |
| Model is missing/corrupt | Use the model installer after download approval. A mismatched existing file must be reviewed before removal/replacement. |
| A shared-context request expires | Capture and review a fresh observation. The API retains its 60-second freshness limit and does not resend automatically. |

Live acceptance was confirmed on the tested Windows 11 ARM64/English setup on
September 16, 2026. General cross-application autonomous repair, other locales,
and all possible devices remain outside that acceptance claim.

See [camera implementation and safety details](../CAMERA_RECOVERY.md),
[model/provider setup](../MODEL_SETUP.md), and [desktop behavior](../../desktop/README.md).
