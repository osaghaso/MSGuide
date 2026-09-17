# MSGuide desktop MVP

A Windows companion for asking by text or voice and getting help with Microsoft
tools. **Guide me** keeps changes with the user; **Fix it for me** requires
approval for each supported action. Current workflows include local Teams camera
diagnosis and approved recovery, local voice transcription, and reviewed screen
context for guidance. This is not unrestricted desktop control. Synthetic
developer workflows are hidden unless explicitly enabled. The
[provider configuration](docs/MODEL_SETUP.md) determines whether approved
public/synthetic screen context uses Copilot or deterministic sample guidance.

**Current test and presentation:** see the
[Teams camera and voice walkthrough](docs/teams-camera-demo/README.md) for setup,
live recovery, permission approvals, voice input, repeat-recording behavior, and
regression commands. Live camera recovery and local voice input were exercised
on Windows 11 ARM64 with English Teams on September 16, 2026. This is not a claim
of arbitrary-app, all-device, or multilingual automation. Historical capture
limits and separate Notepad acceptance remain documented in
[validation](docs/VALIDATION.md) and [Notepad acceptance](docs/NOTEPAD_TASK.md).

## Install and start

Use an unlocked Windows 10/11 interactive desktop with:

- **PowerShell 7** (`pwsh`), not Windows PowerShell 5.1. The launcher uses modern .NET `ProcessStartInfo.ArgumentList` and `Environment` APIs.
- **.NET 10 SDK** for the WPF build.
- **Python 3.11**, available as `python` for initial setup.

From the project root in PowerShell 7:

```powershell
python -m venv venv
.\venv\Scripts\python -m pip install -r requirements-dev.txt
.\scripts\Start-MSGuide.ps1
```

No virtual-environment activation is required. The launcher expects the environment at the root's venv directory. [requirements-dev.txt](requirements-dev.txt) includes [requirements.txt](requirements.txt); these pin direct requirements, not a complete transitive dependency lock.

[scripts/Start-MSGuide.ps1](scripts/Start-MSGuide.ps1) builds the desktop, starts a single-worker loopback API on **port 8765**, checks readiness, and opens the companion. It creates an ephemeral local token, writes no token to disk, and removes `MSGUIDE_MODEL_API_KEY` from the desktop child's environment. On desktop exit or launcher cleanup, it stops its owned server. Keep the launcher running for the desktop session.

- `-Port 8766`: choose a different free port (1024–65535); existing processes are never stopped to free a port.
- `-SkipBuild`: reuse an existing desktop binary; omit after source changes.
- `-IntegrationTest`: run the synthetic desktop harness instead of normal UI; forces the deterministic provider. See [how to run validation](docs/VALIDATION.md).
- `-CaptureTest`: test real demo-window capture and API guidance without requiring foreground activation. This does not test overlay interaction.
- `-Copilot`: use the persistent GitHub Copilot SDK provider for the generic
  snapshot-guidance workflow, pinned to GPT-6 Astra with `xhigh` reasoning and
  the long-context tier. The Teams camera journey keeps its local state,
  target revalidation, approval, and invocation boundary.
- `-CameraFixture`: run the camera recovery card against its clearly labelled
  deterministic Teams-camera-off fixture. Choose **Guide me** or
  **Fix it for me**; fixture completion never claims real camera recovery.
- `-Shareable`: explicitly allow MSGuide and its guidance overlay to appear in
  full-screen sharing. They are excluded from capture by default. Share the
  **Screen**, not an individual Teams window.
- `-DeveloperTools`: show the synthetic Build Center/Notepad workflows and
  manual screen-context diagnostics. Hidden during normal use.

The normal shell starts with a blank **Ask MSGuide** composer and **Guide me** /
**Fix it for me** modes. Task-specific controls appear after a request; Settings
holds voice, connection, and privacy options. When no automated fix is supported,
**Use screen context** offers explicit capture/review/approval for guidance
without implying that a fix has already occurred.

## Try the built-in demo

These older synthetic workflows require `-DeveloperTools`. For the primary live
scenario, use the [camera and voice walkthrough](docs/teams-camera-demo/README.md).

For the new dual-mode task: **Open demo → choose Guide me / Do it for me → Prepare demo task → review and approve → Start approved task**. Guide mode waits for your clicks and **I did it · check**. Control mode invokes View logs and Open troubleshooting, then revokes authority. **Stop task** and **Take over manually** remain available. See [the complete dual-mode instructions](docs/DUAL_MODE.md).

The separate snapshot-guidance workflow is:

1. Click **Open demo**, then invoke MSGuide with **Ctrl+Alt+M**. The hotkey is configurable through `MSGUIDE_HOTKEY`; use the taskbar if registration fails.
2. Enter “Help me find the build error” **before capture**, and select **MSGuide Demo**.
3. Click **Capture / review**. Inspect the actual image and UI Automation text/boxes, including full-size image inspection. Nothing has been uploaded.
4. Leave screenshot sharing off for the default demo. Check explicit consent, then click **Send approved snapshot**. Consent alone does not send anything.
5. Switch to the demo and click the indicated control yourself: **View logs → Open troubleshooting → Mark resolved**.
6. After each click, use **Check next step** for a **fresh capture and review**, approve again, and send. It is not an automatic observation loop. The final demo screen says **Issue resolved**.
7. Use **Pause / clear**, **Dismiss**, Escape, minimize, or Exit to cancel work and clear the current evidence/transcript. Already transmitted data cannot be recalled.

Editing the prompt, changing the selected window, or moving/resizing the captured window invalidates the snapshot. Snapshots expire after 60 seconds. In-window content changes are not all detected: always request a fresh Check after acting.

## Voice

Dictation uses **local Whisper (`small.en`)**, not the legacy Windows dictation
engine or the Copilot model. No Developer Mode, MSIX registration, cloud audio
processing, or speech subscription is needed. Install the model once, only
after approving its approximately 466 MiB download:

```powershell
.\scripts\Install-MSGuideSpeechModel.ps1 -AcceptDownload
```

The installer verifies the model's SHA-256 digest and stores it under
`%LOCALAPPDATA%\MSGuide\models`, outside the repository. The launcher never
downloads models automatically; a missing model fails visibly with no silent
fallback to another recognizer.

Open **Voice settings**, choose the intended **Microphone**, then return to the
question and select **Start microphone**.
With an existing draft, that button becomes **New voice question** and replaces
the old text only after new speech is recognized. Use **Add more** to explicitly
append. Failed or cancelled replacement recordings retain the old draft; typing
while recording cancels pending speech so it cannot overwrite your edits.
The input meter shows incoming audio. **Stop & transcribe** closes the microphone
and transcribes the bounded recording locally; recording auto-stops after 30
seconds. Audio stays in memory and is cleared after processing or cancellation.
Transcription can take several seconds and has a 45-second deadline. During
processing, **Cancel transcription** stops the operation. Review/edit the words,
then select **Ask MSGuide** or press Enter. Speech never submits automatically.

Use **Sound input settings** for hardware mute or input-volume problems; choosing
a microphone in MSGuide does not change Windows' default input. **Speak response**
uses Windows local playback and requires an installed voice. Pause, dismissal,
and exit cancel dictation and discard pending speech callbacks.
If a microphone driver fails to confirm stopping, MSGuide reports **MIC STATUS
UNKNOWN**, prevents another recording, and asks you to close the app; it does
not pretend the microphone is off.

## Privacy and limits

- Manual selected-window capture only; no whole-desktop fallback, periodic capture, or global input injection.
- **Snapshot UI Automation names only, not pixel OCR. No pixel redaction or redaction editor.** Excluding password controls from UIA does not sanitize screenshot pixels or all accessible text. Discard sensitive captures. The separately approved Notepad task reads bounded editor text locally for verification.
- Screenshot upload is opt-in per snapshot. Pillow validates PNGs and strips metadata before remote processing; it does not remove sensitive pixels. The default backend makes no remote model calls.
- Capture data is held in memory and cleared/disposed on completion or cancellation, not deliberately saved by the application. This is not guaranteed forensic memory erasure or a promise about a remote provider's retention.
- Protected, elevated, GPU-rendered, minimized, blank, or unresponsive windows may fail capture. A stuck native capture may require restarting MSGuide.
- No enterprise authentication, permission-aware search, general external automation, production deployment, or approved internal-data pilot. Experimental Notepad control requires explicit opt-in; use synthetic/public data only.

## Documentation

- [docs/teams-camera-demo/README.md](docs/teams-camera-demo/README.md): live test and presentation runbook, expected results, voice behavior, and repeatable checks.
- [docs/NOTEPAD_TASK.md](docs/NOTEPAD_TASK.md): experimental external task, test evidence and acceptance checklist.
- [docs/DUAL_MODE.md](docs/DUAL_MODE.md): implemented Guide/Control fixture, authorization, test results and external-control limits.
- [SUMMARY.md](SUMMARY.md): current status at a glance.
- [IMPLEMENTATION.md](IMPLEMENTATION.md): implemented components and boundaries.
- [docs/API.md](docs/API.md): current routes and contracts.
- [docs/MODEL_SETUP.md](docs/MODEL_SETUP.md): explicit provider configuration and data sharing.
- [docs/VALIDATION.md](docs/VALIDATION.md): commands, evidence, blockers, and documentation conflicts.
- [PLAN.md](PLAN.md) and [ROADMAP.md](ROADMAP.md): evidence-based progress and remaining gates.
- [desktop/README.md](desktop/README.md): desktop behavior and platform limitations.
