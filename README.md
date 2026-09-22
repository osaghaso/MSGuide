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
- **.NET SDK 10.0.400** (or a compatible patch allowed by `global.json`) for WPF.
- **Windows x64 CPython 3.11.9**, available as `python` for the checked-in Python locks. ARM64 Windows can use x64 Python; a native ARM64 Python lock is not claimed.

From the project root in PowerShell 7:

```powershell
python -m venv venv
.\venv\Scripts\python -m pip install --require-hashes --only-binary=:all: -r requirements-dev.lock.txt
dotnet restore .\desktop\MSGuide.Desktop.csproj --locked-mode
.\scripts\Start-MSGuide.ps1
```

No virtual-environment activation is required. The launcher expects the root's venv directory. The portable `requirements-dev.lock.txt` contains the tested runtime and test dependency closure; use `requirements.lock.txt` instead for runtime-only installation, retaining `--require-hashes --only-binary=:all:`. These files pin versions and wheel hashes without embedding workstation-specific package-mirror URLs or unrelated environment packages. Ordinary installation does not require experimental lockfile support or pip 26.2.1.

Keep direct dependency intent in `requirements.txt` / `requirements-dev.txt`. Regenerate locks intentionally after manifest changes using `scripts\lock_python_dependencies.py` with pip 26.2.1; the helper converts native pip lock output using the standard library. Do not weaken TLS validation to resolve package-source problems. NuGet lock files cover the desktop and capture probe, and launcher builds use locked restore.

[scripts/Start-MSGuide.ps1](scripts/Start-MSGuide.ps1) builds the desktop, starts a single-worker loopback API on **port 8765**, checks readiness, and opens the companion. Readiness has a 60-second budget, leaving headroom beyond the provider's 30-second startup deadline; failures identify the readiness stage and diagnostic log. Press `Ctrl+Alt+M`, or click the stationary logo, to open the compact prompt. Camera recovery, permission approvals, voice, Settings, manual screen review, and task progress all use this one scrollable view; there is no separate Details step.

An approved generic observation requests a structured **plan segment**: up to **32 ordered steps**, ending at suggested completion or an explicit resource, information, permission, observation, unsupported-operation, or plan-limit boundary. The entire plan is validated before its first action. In **Fix it for me**, one model call can drive multiple steps using fresh local UIA checks, not one model call per click. Execution continues without an eight-action or two-minute pause and verifies every action. After a segment makes safe progress, `plan_limit` and `observation` boundaries automatically refresh the same approved resource and request the next segment. The 32-step response limit is not an execution checkpoint. A changed resource, missing/changed target, incomplete evidence, required input/permission, unsupported operation, cancellation, no progress, or unknown outcome still stops the run. An empty plan never triggers repeated automatic planning. **Review & continue** is for resumable interruptions, not routine step counts; suggested completion still requires review. The existing 10,000-decision protocol ceiling remains a safeguard against pathological runs.

A task retains its original request, plan/cursor, task/step IDs, last 16 action outcomes, and bounded clarification text in memory. Repeated controls are allowed on progressed states; every step is uniquely rebound locally and its native target/state is checked again. Deferred writes require an empty writable non-password field; observed writes require the unchanged reviewed value digest. Unknown outcomes and cancelled queued work cannot resume. Generic goal completion remains **not independently verified**, even after observed control effects and a model completion suggestion.

The normal hotkey remains **Ctrl+Alt+M**. Other keys require an explicit
`MSGUIDE_HOTKEY` override; they are not a new default. Close an older MSGuide copy
before relaunching an updated build. If registration conflicts, the new copy
keeps the compact prompt visible with a taskbar entry instead of hiding with no usable hotkey.

**Settings > Companion position** contains the **Follow pointer**
option. Turn it off to pin the companion in place. Drag the pinned logo or the
**Move** control in Settings to reposition it; focus **Move** and use arrow
keys for keyboard movement (Shift+arrow moves one pixel). The choice and position
are saved locally on this PC, with off-screen positions brought onto an available
monitor. This is independent of **Guide me / Fix it for me**. An approved action
can still show its click-through target marker, then return to the pinned position.
Only this display preference and coordinates are saved, not prompts or approvals.
Click the stationary logo to open the same prompt as **Ctrl+Alt+M**; dragging
repositions it without opening the prompt. The logo has no rectangular button
frame. The following logo and action markers remain click-through.

The compact prompt has a 44-DIP **microphone icon** beside Ask that becomes a stop icon
while recording, with clear text/tooltips and screen-reader labels. **Append**
explicitly adds to a draft; cancellation stays immediately available while
processing or input closure is uncertain. The input meter, warnings, and
**Settings > Microphone options** retain input selection/refresh and idle audio controls. It shares the same draft
and local speech lifecycle as the rest of the app. Opening the prompt never starts recording;
closing it cancels pending voice input. Transcription never submits the question.

For **Fix it for me**, submitting a Teams camera request authorizes one
freshly revalidated camera-on action for the selected meeting/prejoin. MSGuide
inspects first, opens Camera settings if needed, and continues through local
checks automatically; there are no routine Next buttons. Device-wide camera
access, user-app access, Teams permission, and Teams restart still require
separate approvals in the compact view. Permission-on and camera-on alone do not
prove readiness. Stop, dismissal, edits, mode changes, and changing the selected
Teams window revoke pending camera-on authority. Ambiguous windows, unsupported
surfaces, policy blocks, and unknown outcomes require a handoff, not retries.
**Guide me** still leaves changes to the user.
Both modes are shown as direct, mutually exclusive choices with a visible
selection mark. Click the mode you want; selecting the already-active mode does
nothing. Choosing the other mode stops pending work and clears its approvals
before switching. It never submits the draft or restarts a repair automatically.

MSGuide identifies the meeting/prejoin by a fresh, complete read of its camera
controls, rather than selecting Teams' home/chat window by title. A disabled
camera can identify the meeting without authorizing any action. The invoked
window is preferred only when its camera surface is verified; otherwise the
unique matching meeting is selected. Multiple matching meetings or incomplete
reads still require clarification. The compact prompt fits its content rather
than holding a full-size workspace open, while retaining its position during
status changes. Read-only **Check again** results stay in the
same camera card. **Fix camera** starts a fresh, separately requested repair.
The current action, permission scope, Stop, and any restart/error warning stay
in the main view; secondary progress and controls are under **More options**.
Verified completion shows **Resolved · camera ready** with **Done** as the main
action and **Check again** secondary. Done clears the finished question and
returns to the small companion without changing the camera or its permissions.
Already-working results still say no change was needed; fixtures say Fixture
complete, never real resolution.

Generic Fix-mode actions are presented **in the foreground**: the target is
outlined and the Windows-logo marker moves onto it before invocation. The app
can return focus from its own companion to the approved window, but never steals
focus from an unrelated application or falls back to background input. Switching
away, cancellation, or a stale target prevents the action.

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
  launch consent authorizes bounded planned semantic actions after fresh local grounding
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
freshly grounded planned semantic actions (`invoke`, `toggle`, `select`, `expand`,
`collapse`, `set_value`, or `scroll`) in Fix mode. `set_value`
replaces an entire writable, non-password field with an explicit value of at
most 1000 characters; its previous value fingerprint must still match. `scroll`
moves one small UIA increment in an explicitly allowed direction. Unsupported
editors, read-only/password fields, incomplete trees, and non-actionable surfaces
require a handoff; there is no coordinate/keyboard fallback. Guide mode can still
describe an approved partial tree/image without executing it.

## Try the built-in demo

These older synthetic workflows require `-DeveloperTools`. For the primary live
scenario, use the [camera and voice walkthrough](docs/teams-camera-demo/README.md).

For the new dual-mode task: **Open demo → choose Guide me / Do it for me → Prepare demo task → review and approve → Start approved task**. Guide mode waits for your clicks and **I did it · check**. Control mode invokes View logs and Open troubleshooting, then revokes authority. **Stop task** and **Take over manually** remain available. See [the complete dual-mode instructions](docs/DUAL_MODE.md).

The easiest real Clicky-style test is:

1. Start with `.\scripts\Start-MSGuide.ps1 -Copilot`. The full workspace stays hidden and a small Windows-logo buddy follows the pointer.
2. Put Calculator or another supported desktop app in the foreground, move the pointer near the control you want help with, and press `Ctrl+Alt+M`.
3. Ask a concrete question in the compact prompt, such as `Where do I clear this calculation?`, then press Enter. The prompt disappears, the buddy shows its thinking state, and the foreground app is captured under the launch-time screen-context grant.
4. In **Guide me**, follow the descriptive plan yourself. Choose **Fix it for me** in the compact prompt to use the launch grant. A mode change revokes old queued authority. Fix mode reacquires each exact fresh UIA target, invokes once, and observes the result. Reopen the compact prompt to review/continue, answer a clarification, or stop the retained task. Suggested completion is not proof.

For the deterministic developer harness, start with `.\scripts\Start-MSGuide.ps1 -DeveloperTools`, open the compact prompt, expand **Developer tools**, and choose **Start demo · capture locally**.
2. Inspect the actual image and UI Automation text/boxes. Leave screenshot sharing off, check explicit consent, then click **Send approved snapshot**.
3. Briefly switch to the demo to see the target. The manual developer harness retains its explicit action button; `-Copilot` sessions act automatically only in Fix mode. The
   click-through outline includes the floating Windows-logo marker requested for
   the desktop UI.
4. Use **Check next step**, review/send the fresh capture, and repeat for **View logs → Open troubleshooting → Mark resolved**. The final demo screen says **Issue resolved**.
5. Use **Pause / clear**, **Dismiss**, Escape, minimize, or Exit to cancel work and clear the current evidence/transcript. Already transmitted data cannot be recalled.

Editing the prompt replaces the old task. Changing the selected window or moving/resizing it invalidates current evidence. Evidence still expires after 60 seconds: SDK/API/desktop guidance waits use the **remaining** lifetime, with 10/8/6 seconds reserved respectively. They do not extend evidence validity. Native actions are caller-bounded to eight seconds; a hung COM call cannot be interrupted safely and blocks new actions until it returns or MSGuide is restarted.

The initial plan request and an explicitly requested replan can share an approved screenshot; steps inside the segment and post-action checks stay **local UIA-only**, reusing suitable post-action evidence for the next binding. Verification retries observations, not actions: up to six reads within five seconds. Semantic actions require their expected effect; only `invoke` can use a stable screen change, which is not causal or goal proof. Logical `controlId` survives label/position changes for verification; the separate `targetId` still binds the exact reviewed state before invocation.

Queued execution requires a stable selected-window resource scope. Supported English Microsoft Edge and Chrome windows can establish a page scope from one visible HTTP(S) address control in browser chrome, outside page documents, with one visible document surface. The address is hashed locally and rechecked before actions; ambiguous, unsupported, or changed page identity still stops execution. Other document trees without proven file/site identity require handoff. New windows are not selected automatically. SDK sessions remain isolated, and remaining plan/history is untrusted context, not cached execution authority.

UIA inspection caches bounded per-node properties and prioritizes actionable
controls plus scope markers in the 200-element export. Shortening decorative
text/context alone no longer disables an otherwise complete control scan.
Actual traversal/provider failures or too many action controls still fail
closed. Camera diagnosis retains its stricter complete-context requirement.

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
question and select the **microphone icon**.
With an existing draft, it is labeled **Replace with voice** and replaces
the old text only after new speech is recognized. Use **Append** to explicitly
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
During shutdown, **MIC STOPPING** remains visible and recording/input changes stay
disabled until hardware closure is acknowledged. After a three-second missing-ack
deadline, **MIC STATUS UNKNOWN** remains latched; a late confirmed closure can
clear the gate but cannot revive cancelled speech. A permanently stuck driver
may require closing the app. Model-load/factory, processor-creation and inference
timings are recorded separately without audio or transcript content. No factory
cache was added, and native performance was not benchmarked by the fake tests.

## Privacy and limits

- Selected-window evidence only; no whole-desktop fallback or global input injection. Session-approved task runs use bounded, action-driven observations, not an always-on monitor. Semantic field replacement and small scrolling require supported UIA patterns; arbitrary typing, coordinate clicks, dragging, and shell commands are not supported.
- **Snapshot UI Automation names only, not pixel OCR. No pixel redaction or redaction editor.** Excluding password controls from UIA does not sanitize screenshot pixels or all accessible text. Discard sensitive captures. The separately approved Notepad task reads bounded editor text locally for verification.
- Manual screenshot upload is opt-in per snapshot; the explicit Copilot launch grant covers initial planning and explicitly reviewed replanning observations. Pillow validates PNGs and strips metadata before remote processing; it does not remove sensitive pixels. Routine planned steps do not upload another observation. Local camera verification remains separate and is not automatically uploaded.
- Images/current observations are cleared on stop. The original task, bounded plan/cursor, last 16 action records, and clarification/checkpoint text remain in local memory until a new prompt, Pause/clear, dismissal, or exit. No durable task store is added. This is not forensic memory erasure or a promise about remote retention.
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
