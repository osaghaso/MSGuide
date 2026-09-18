# MSGuide desktop MVP

A Windows companion for asking by text or voice and getting help with Microsoft
tools. **Guide me** never executes actions, including in a `-Copilot` session.
**Fix it for me** uses only supported, freshly revalidated actions under the
launch-time automation grant; manual developer sessions require action approval.
Current workflows include local Teams camera
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

[scripts/Start-MSGuide.ps1](scripts/Start-MSGuide.ps1) builds the desktop, starts a single-worker loopback API on **port 8765**, checks readiness, and opens the companion. Normal launch shows a click-through Windows-logo buddy beside the pointer. Press `Ctrl+Alt+M` to open the compact prompt; **Details** holds mode selection, the retained task checkpoint, and manual context review.

In **Fix it for me**, Copilot sessions execute at most **eight actions per explicitly continued batch**, with fresh post-action checks including the eighth action. A task keeps its original request, task/step IDs, the last 16 action outcomes, and a bounded model checkpoint in memory. **Review & continue** gets fresh evidence and preserves that context; its optional reply box answers clarification questions without replacing the original goal. Reusing a control is allowed when the observed state has progressed. Unchanged/repeated states stop without an action retry. Questions, blocked steps, failures, cancellation, budget exhaustion, and suggested completion are distinct states, not success. Generic goal completion is **not independently verified**; a model completion suggestion requires user review. Unknown action/verification outcomes cannot be continued automatically.

The launcher creates an ephemeral local token, writes no token to disk, and removes model credentials from the desktop child's environment. On exit it stops its owned server. Bounded rotating diagnostics under `%LOCALAPPDATA%\MSGuide\logs` connect task/step IDs with capture, guidance, invocation, verification, and stop timings; they exclude prompts, labels, typed values, screenshots, and model prose.

- `-Port 8766`: choose a different free port (1024–65535); existing processes are never stopped to free a port.
- `-SkipBuild`: reuse an existing desktop binary; omit after source changes.
- `-IntegrationTest`: run the synthetic desktop harness instead of normal UI; forces the deterministic provider. See [how to run validation](docs/VALIDATION.md).
- `-CaptureTest`: test real demo-window capture and API guidance without requiring foreground activation. This does not test overlay interaction.
- `-Copilot`: use the persistent GitHub Copilot SDK provider for the generic
  snapshot-guidance workflow, pinned to GPT-6 Astra with low reasoning and
  the default context tier for interactive latency. Launching with this explicit switch grants screen
  context for that process lifetime: invoking MSGuide from an app and asking a
  question captures and sends that foreground window automatically. The same
  launch consent authorizes one freshly grounded semantic action per response
  only in **Fix it for me**. **Guide me** remains non-executing. The Teams camera journey keeps its local state,
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
holds voice, connection, and privacy options. When the requested task is not the camera workflow, **Use screen context**
opens explicit capture/review/approval. An explicit `-Copilot` launch authorizes
one freshly grounded semantic action (`invoke`, `toggle`, `select`, `expand`,
`collapse`, `set_value`, or `scroll`) per response in Fix mode. `set_value`
replaces an entire writable, non-password field with an explicit value of at
most 1000 characters; its previous value fingerprint must still match. `scroll`
moves one small UIA increment in an explicitly allowed direction. Unsupported
editors, read-only/password fields, incomplete trees, and non-actionable surfaces
require a handoff; there is no coordinate/keyboard fallback.

## Try the built-in demo

These older synthetic workflows require `-DeveloperTools`. For the primary live
scenario, use the [camera and voice walkthrough](docs/teams-camera-demo/README.md).

For the new dual-mode task: **Open demo → choose Guide me / Do it for me → Prepare demo task → review and approve → Start approved task**. Guide mode waits for your clicks and **I did it · check**. Control mode invokes View logs and Open troubleshooting, then revokes authority. **Stop task** and **Take over manually** remain available. See [the complete dual-mode instructions](docs/DUAL_MODE.md).

The easiest real Clicky-style test is:

1. Start with `.\scripts\Start-MSGuide.ps1 -Copilot`. The full workspace stays hidden and a small Windows-logo buddy follows the pointer.
2. Put Calculator or another supported desktop app in the foreground, move the pointer near the control you want help with, and press `Ctrl+Alt+M`.
3. Ask a concrete question in the compact prompt, such as `Where do I clear this calculation?`, then press Enter. The prompt disappears, the buddy shows its thinking state, and the foreground app is captured under the launch-time screen-context grant.
4. In **Guide me**, perform the suggested step yourself. To authorize execution, choose **Fix it for me** in Details before asking. Fix mode reacquires each exact fresh UIA target, invokes once, and observes the result. The final state stays visible in the buddy and Details; suggested completion is not proof. **Review & continue** resumes a reviewed checkpoint, never an unknown action.

For the deterministic developer harness, start with `.\scripts\Start-MSGuide.ps1 -DeveloperTools`, open **Details**, expand **Developer tools**, and choose **Start demo · capture locally**.
2. Inspect the actual image and UI Automation text/boxes. Leave screenshot sharing off, check explicit consent, then click **Send approved snapshot**.
3. Briefly switch to the demo to see the target. The manual developer harness retains its explicit action button; `-Copilot` sessions act automatically only in Fix mode. The
   click-through outline includes the floating Windows-logo marker requested for
   the desktop UI.
4. Use **Check next step**, review/send the fresh capture, and repeat for **View logs → Open troubleshooting → Mark resolved**. The final demo screen says **Issue resolved**.
5. Use **Pause / clear**, **Dismiss**, Escape, minimize, or Exit to cancel work and clear the current evidence/transcript. Already transmitted data cannot be recalled.

Editing the prompt replaces the old task. Changing the selected window or moving/resizing it invalidates current evidence. Evidence still expires after 60 seconds: SDK/API/desktop guidance waits use the **remaining** lifetime, with 10/8/6 seconds reserved respectively. They do not extend evidence validity. Native actions are caller-bounded to eight seconds; a hung COM call cannot be interrupted safely and blocks new actions until it returns or MSGuide is restarted.

The first automatic task observation can share its approved screenshot; subsequent steps and post-action checks inspect **fresh UIA metadata only**. Verification retries observations, not actions: up to six reads within five seconds, looking for a supported control effect or a stable screen change. Neither proves the overall goal. SDK sessions remain isolated, with bounded history sent explicitly; accepted results no longer wait for session teardown. Remote inference and session creation still take time.

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

- Selected-window evidence only; no whole-desktop fallback or global input injection. Session-approved task runs use bounded, action-driven observations, not an always-on monitor. Semantic field replacement and small scrolling require supported UIA patterns; arbitrary typing, coordinate clicks, dragging, and shell commands are not supported.
- **Snapshot UI Automation names only, not pixel OCR. No pixel redaction or redaction editor.** Excluding password controls from UIA does not sanitize screenshot pixels or all accessible text. Discard sensitive captures. The separately approved Notepad task reads bounded editor text locally for verification.
- Manual screenshot upload is opt-in per snapshot; the explicit Copilot launch grant covers the first automatic task image. Pillow validates PNGs and strips metadata before remote processing; it does not remove sensitive pixels. The default backend makes no remote model calls.
- Images/current observations are cleared on stop. The original task, last 16 action records, and bounded clarification/checkpoint text remain in local memory for review until a new prompt, Pause/clear, dismissal, or exit. No durable task store is added. This is not forensic memory erasure or a promise about remote retention.
- Protected, elevated, GPU-rendered, minimized, blank, or unresponsive windows may fail capture. A stuck native capture may require restarting MSGuide.
- No enterprise authentication, permission-aware search, general external automation, production deployment, or approved internal-data pilot. Experimental Notepad control requires explicit opt-in; use synthetic/public data only.
- The desktop is the real semantic-action executor. `/v1/actions/*` and `/v1/jobs/*` remain the separate 250 ms in-memory **mock simulator**, not a task queue or desktop execution/status service.

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
