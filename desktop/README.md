# MSGuide Desktop — native guide and approved-action MVP

WPF / .NET 10 Windows companion. Local dictation uses **Whisper.net 1.9.1**,
its CPU runtime (including Windows ARM64), and **NAudio.WinMM 2.2.1** for
microphone capture. **System.Speech 10.0.0** supplies playback and synthetic
test audio, not the interactive dictation engine. A .NET 10 SDK is required to
build; the root setup uses PowerShell 7 and Windows x64 CPython 3.11.9, including
x64 Python on ARM64 Windows. Install portable `requirements-dev.lock.txt`
(runtime plus tests) or `requirements.lock.txt` (runtime only) with
`--require-hashes --only-binary=:all:`. Pip 26.2.1 is needed for lock regeneration,
not ordinary installation;
see the [root setup commands](../README.md#install-and-start). No cloud
provisioning or Developer Mode is needed.

## Build and start

Build with `dotnet build desktop/MSGuide.Desktop.csproj` from the repository root using the installed .NET 10 SDK. Run `dotnet run --project desktop/MSGuide.Desktop.csproj` from the authenticated launcher's environment, or launch `desktop/bin/Debug/net10.0-windows/MSGuide.Desktop.exe` from that environment. The parent launcher owns starting the backend and generating the ephemeral token.

- `MSGUIDE_LOCAL_TOKEN`: required for `/v1` calls; inherited from the launcher, never embedded or persisted by this client.
- `MSGUIDE_API_URL`: default `http://127.0.0.1:8000`. Only a literal loopback HTTP origin or `localhost` is accepted; localhost is pinned to 127.0.0.1. Proxies and redirects are disabled to prevent forwarding the token.
- `MSGUIDE_HOTKEY`: optional, default `Ctrl+Alt+M`; examples `Ctrl+Shift+G` or `Alt+Shift+M`. Normal launch keeps the full workspace hidden and shows a click-through Windows-logo buddy 35 pixels right and 25 pixels below the pointer. The hotkey remembers the foreground app and opens a compact prompt beside the pointer. Its thinking and response bubble tracks the pointer at roughly 60 FPS, flips at screen edges, and never intercepts clicks. **Details** opens the full workspace for manual review, settings, and action approval. A registration conflict is reported in Details.
- Expected backend: `GET /health` with status `ok`, version `0.2.0`, mode `demo`/`model`; authenticated `POST /v1/sessions` and `POST /v1/guidance`. No legacy assist/action routes are called.

## Try the supported workflow

1. Start the local backend and desktop through the parent launcher. The Windows-logo buddy appears beside the pointer; the full workspace does not open first. Press `Ctrl+Alt+M` over the foreground app, choose Guide/Fix in the compact prompt, enter a question, and press Enter. Reopen it for the retained plan, boundary, clarification, continuation, and Stop. The buddy stays click-through. **Details** provides manual review and expanded controls: **DEMO** is deterministic sample guidance; **MODEL** is the configured provider, not enterprise authorization.
2. Click **Open demo**. A separate **MSGuide Demo** window shows a synthetic build dashboard; only **View logs** is initially available.
3. Invoke the companion with the hotkey, type “Help me find the build error”, select **MSGuide Demo**, and click **Capture / review**.
4. Inspect the actual image (expand full-size inspection if needed) and UI Automation text/element metadata. Pixel sharing defaults **off**. Check consent, optionally enable image sharing for a vision provider, then click **Send approved snapshot**. No capture is uploaded without this click.
5. Switch to the demo to see the nonactivating, click-through outline and Windows-logo marker. Guide mode never executes. Copilot launch sessions can auto-execute in **Fix it for me** only; manual developer sessions retain their approval button. **View logs** reveals “Build failed: exit code 1” and **Open troubleshooting**; that reveals “Check compiler errors and missing dependencies” and **Mark resolved**; the last step shows “Issue resolved”. Old workflow buttons are removed. All changes are synthetic and local.
6. **Check next step** starts a fresh capture/review, never a blind resend. Repeat approval at each step. **Reset demo** restarts the sandbox.
7. Select a **Microphone**, then **Start microphone** / **Stop & transcribe** for local Whisper dictation. Recording stops at 30 seconds and is transcribed after microphone closure. Input levels and low-volume diagnostics are visible. Review/edit the transcript before **Ask MSGuide**; submission is disabled until transcription finishes. Use **Cancel transcription** while local processing is active. **Sound input settings** opens Windows' volume/mute controls without changing them. **Speak response** is optional local playback.
8. **Pause / clear**, **Dismiss**, Escape, minimize, or Exit cancel work, clear snapshot references/bytes and transcript, stop microphone/speech, and hide highlights. Already transmitted data cannot be recalled. A title-bar close exits the application.

## Privacy and support boundaries

- Selected-HWND Windows Graphics Capture, with **no desktop/PrintWindow fallback or global input injection**. Manual snapshots require review/upload approval; the explicit Copilot launch grant allows task-driven captures. Fix mode supports UIA `invoke`, `toggle`, `select`, `expand`, `collapse`, `set_value`, and `scroll`, one action at a time after exact target reacquisition. Guide mode never executes, even with a launch grant.
- `set_value` requires a writable, non-password ValuePattern field containing at most 1000 characters and an explicit full replacement of at most 1000 characters. The field's previous value digest is checked again before replacement. Read-only/password/large unsupported editors are not typed into. `scroll` takes exactly one small increment in a freshly available direction. Unsupported surfaces require manual handoff, never coordinate or keyboard fallback.
- Full window bounds use physical coordinates, matching the bitmap and normalized UIA boxes. PNGs are limited to 1280 pixels on the longest side and 2,000,000 bytes. Large physical allocations are rejected. Captures stay in memory; the application writes no screenshots, transcripts, tokens, UI text, prompts, or model output to disk. Bounded rotating operational diagnostics contain only timestamps, endpoints, dimensions/counts, lifecycle stages, status/error codes, exception types, and correlation IDs under `%LOCALAPPDATA%\MSGuide\logs`. .NET/WPF may retain temporary managed/native copies until collection; this is not a forensic memory-erasure guarantee.
- UI Automation names are collected with bounded traversal, not pixel OCR. Offscreen/password subtrees and disabled target controls are excluded. **This is not image redaction**: screenshot pixels and other accessible text can still contain passwords or sensitive information. There is no redaction editor. Do not approve sensitive content; discard it. A backend model may process approved content remotely even though the client connects only to loopback.
- Some GPU, elevated, protected, minimized, or unresponsive windows cannot be captured. Blank/uniform images are rejected heuristically (not guaranteed detection). UIA can be unavailable or incomplete. Inspect the preview rather than assuming capture success means all pixels are valid.
- Capture/UIA calls can block inside native providers. Capture callers wait at most 30 seconds; action callers at most **eight seconds**, including target lookup. Lookup is a selected-root raw-tree walk, capped at 800 nodes, depth 32, and three seconds between native calls; an incomplete search cannot claim a unique match. At most one native action is outstanding. Cancellation before invocation seals the invocation gate; after invocation starts, timeout/cancellation means **unknown outcome**, not failure-to-act. Late returns never advance a task, and new actions/captures are blocked while the worker remains active. Cooperative tokens cannot interrupt a hung COM call: permanent hangs require restarting MSGuide; process isolation remains deferred.
- Snapshot TTL is 60 seconds, measured conservatively from the earliest capture evidence, not after encoding/inspection. SDK/API/desktop guidance waits reserve 10/8/6 seconds of the remaining TTL (caps 50/52/54 seconds). Focus/window/target checks still run immediately before action. Moves, resizes, minimization, disappearance, approval revocation, edits, and supersession cancel current work. Exact response/task/step IDs and semantic input are checked; low-confidence, password, offscreen, ambiguous, changed-value, and mismatched targets are rejected.
- Native overlay placement uses physical desktop coordinates and a PerMonitorV2 manifest; WPF draws the border in local DIPs. Negative origins and scale math have executable checks, but mixed-DPI monitor rendering/straddling windows still require runtime verification. Capture exclusion via `SetWindowDisplayAffinity` is best effort, not a security guarantee.
- Citation URLs never open automatically; only a user click opens an HTTPS source. Other schemes display as non-clickable text. HTTPS does not imply that a source is trusted.
- No tray dependency, enterprise sign-in, authorized enterprise retrieval, always-on voice, OCR engine, arbitrary keystrokes/coordinates, dragging, unrestricted automation, durable job system, or deployment is included.

## Generic task progress and continuation

`ScreenTaskSession.RunAsync` is the production generic loop and offline test
seam. An approved observation requests up to 32 ordered plan steps through the next
resource/information/permission/observation/unsupported/plan-limit boundary or a
completion suggestion. Both providers and the API validate the entire segment;
the desktop validates it again before any action. Legacy single-step results
remain an explicit compatibility path, not the normal desktop request.

The loop retains the prompt, plan/cursor, task ID, step, last 16 action results and
bounded clarification text in memory. Fix mode executes continuously without an
eight-action or two-minute checkpoint. After a segment makes progress, a
`plan_limit` or `observation` boundary refreshes the approved screenshot and
requests the next plan automatically, but only while the same identified resource
remains completely inspectable. Empty plans cannot trigger an inference loop.
Other boundaries or failed grounding require review, with a reason and needed input.
Per-operation deadlines and the 10,000-decision protocol ceiling remain. Mode changes
revoke old queued authority; unknown and cancelled work cannot resume. New prompts,
Pause/clear, dismissal and exit discard context; there is no durable task store.

Initial planning, same-resource automatic refreshes, and explicitly reviewed
replanning may include an approved screenshot. Inside a segment, binding and verification use local UIA-only
observations, reusing suitable post-action evidence for the next step. No new
model call is made for a routine expected control change. After invocation,
verification makes at most six reads within five seconds, waiting 250 ms only
between reads. Toggle, value replacement, selection, expansion/collapse, and
scroll actions require their expected semantic effect; unrelated UI changes
cannot substitute for it. If that effect is still missing after the bounded
checks, the outcome is `unknown` and continuation is disabled. Only `invoke`,
which has no generic semantic postcondition, can use two consecutive stable
changed UIA states. Reads, not actions, are retried. Every action is observed
before continuing. Repeated controls are valid on progressed states;
unchanged/repeated states stop, and continuing an unchanged no-progress
checkpoint cannot replay its action.

Observed targets are anchored by stable `controlId` (selected window, provider
process and nonempty runtime ID); label/box changes can still be recognized during
effect verification. The separate reviewed `targetId`, exact label/box/action,
availability, password/read-only/value and window checks remain enforced before
invocation. Missing or ambiguous identities cannot authorize planned actions.
Deferred target intents match exact role/label/action and any declared metadata,
never fuzzy text or first-match selectors. Deferred writes require an empty field;
observed writes require the unchanged prior value hash.

Resource scope is conservative: selected HWND identity and caption must remain
stable. A generic document tree cannot establish the current file/site identity
and therefore requires handoff instead of queued execution. New windows/resources,
permissions and credentials are not acquired automatically. Guide mode can
describe approved partial text/images, but incomplete evidence cannot execute.

Statuses distinguish `checkpoint`, `needs_input`, `blocked`, `no_progress`,
`cancelled`, `failed`, `unknown`, and `review_required`. An invocation returning
is not progress; a changed screen is not causal proof; neither proves the goal.
Generic completion suggestions remain **not independently verified** and retain
context for review. Unknown invocation/verification outcomes disable continuation.
Capture-access denials retain a `failed` checkpoint before invocation, or an
`unknown` outcome while checking an invoked action; uncertain actions are not
replayed. Companion action results use their recorded step numbers, not the
invocation count, including across continuation and the rolling history bound.
The buddy no longer auto-hides final task states; the compact prompt and Details
retain the plan/cursor, boundary and needed input. They share the same mode,
continue/reply and Stop handlers, not separate task engines. Current evidence is disposed on
stop; bounded task text stays only until explicit clearing/replacement/exit.

The camera and demo adapters keep their independent local state/verifiers and
approval rules. Camera UIA actions share the single-in-flight native action
guard; experimental Notepad control retains its opt-in adapter. The desktop is
the executor: `/v1/actions/*` and `/v1/jobs/*` are still the unrelated in-memory
250 ms mock simulator, not the progress source for desktop tasks.

## Runnable checks and verification

Run `desktop\bin\Debug\net10.0-windows10.0.19041.0\MSGuide.Desktop.exe --self-test` (or `dotnet run --project desktop\MSGuide.Desktop.csproj -- --self-test`). This exits 0 on success, 1 on failure, without showing a window or connecting to the backend. Checks cover loopback URL rejection, unsafe citation schemes, freshness boundaries, response echo mismatch, malformed target boxes, and physical target mapping at 100/125/150/200% scale with a negative desktop origin.

Self-tests also run the actual generic loop with synthetic observations and fake
guidance/actions: multi-step history, legitimate repeated controls, no progress,
continuous execution beyond eight actions, bounded history, all stop states,
Guide-mode non-execution, semantic value/scroll effects, cancellation/supersession,
unrelated UI churn with missing/late semantic effects, capture-access failures
before/after invocation, result notifications across continuation/history rollover,
and a fake hung native worker. A delayed HTTP handler verifies cancellation at the
remaining freshness deadline. Camera state/consent tests use fixtures only.
Plan regressions assert one model call for complete three-, 17-, and 31-step runs,
automatic continuation across 32-step plan limits and same-resource observation
boundaries, scope/cancellation checks before refreshed inference, every-step
verification, whole-plan rejection of a malformed third
step, resource/target drift, cancelled/unknown queues, stable logical identity,
partial Guide context and actual compact-control handlers. No test opens the
interactive companion or invokes real app controls.
Build with `dotnet build .\desktop\MSGuide.Desktop.csproj --no-restore --output <unique-artifact-directory>`
and run that directory's `MSGuide.Desktop.exe --self-test --test-results <absolute-json-path>`
to avoid replacing an active desktop binary. Disable session grants and synthetic
speech/Whisper opt-ins when running these noninteractive checks. These tests do not
claim live-app UIA or remote-model acceptance.

Install the local English model with
`scripts\Install-MSGuideSpeechModel.ps1 -AcceptDownload` after approving the
466 MiB download. Its verified model file stays under
`%LOCALAPPDATA%\MSGuide\models`, never in the repository. Normal startup never
downloads or substitutes another speech model automatically.

Speech lifecycle self-tests use fake input and never open a microphone. Cancel
invalidates transcript callbacks immediately but retains hardware completion
handling. Recording/device changes remain gated while `MIC STOPPING` or
`MIC STATUS UNKNOWN`; a three-second missing acknowledgement is explicit, and a
late acknowledgement can clear the hardware gate without reviving transcription.
The main Pause banner also distinguishes pending shutdown, unknown microphone
state and confirmed inactivity. Late audio notifications update that banner only
while the same paused UI still owns it, never a newer task or status message.
Whisper model/factory load, processor construction and inference have separate
content-free timing events. No factory reuse or native speedup is claimed.
Set `MSGUIDE_WHISPER_SYNTHETIC_TEST=1` for real local Whisper transcription of
synthetic speech and silence. These use memory only, with no speaker output,
microphone input, audio file, or remote call. The older
`MSGUIDE_SPEECH_SYNTHETIC_TEST=1` checks the legacy Windows test adapter only;
it is not proof of Whisper accuracy.

### Capture-only runtime check (parent starts backend)

`--capture-test` is a separate, narrower test, mutually exclusive with `--integration-test` and `--self-test`. Use the parent's existing `MSGUIDE_API_URL` and `MSGUIDE_LOCAL_TOKEN`; health must report `ok`, `demo`, `0.2.0`. The harness never starts a backend or generates credentials. Parent launcher support is separate work.

It renders its own actual DemoWindow with `ShowActivated = false`, awaits `ContentRendered`, and passes only that instance's HWND/PID to the real CaptureService. It does not call Activate or require foreground ownership. Selected-window Windows Graphics Capture/UIA support must be available; an unavailable desktop, unsupported/blank capture, or missing UIA still fails, never skips or fabricates evidence.

All four real states must contain exactly the expected workflow heading and button labels (and no old workflow labels). Checks validate bounded metadata/boxes, real PNG/preview presence, metadata-only ApiClient requests, matching response IDs, observed target labels/boxes, next-step/completed status, and a null completed target. Only owned demo buttons are invoked through their WPF automation peers on the dispatcher. Every snapshot is disposed, its PNG bytes cleared and references released, and disposed observations rejected; moving the owned window invalidates the final snapshot's bounds. Owned windows/evidence are cleaned up on failure too.

The JSON report identifies `test: "capture"`, not integration. It defaults to `capture-results.json` in the working directory; the parent should pass `--test-results desktop/obj/capture-results.json` to keep it separate from integration results. Exit codes and sanitized report fields follow the integration format below. Four-minute overall and existing per-capture timeouts apply.

**This does not test foreground retention, overlay rendering/placement/click-through, real mouse input, or MainWindow pause/supersession.** Passing capture-only does not unblock or replace the foreground integration test. Build/self-test alone do not verify capture-only runtime behavior; that run remains pending the parent's backend environment.

### Real foreground runtime integration (parent starts backend)

Run from an unlocked, visible interactive Windows session with the parent's existing `MSGUIDE_API_URL` and `MSGUIDE_LOCAL_TOKEN` environment. The service must report **demo** mode, healthy API v0.2.0. No backend is started, no credentials are generated, no cloud endpoints are contacted, and no window chooser is used by this harness.

```powershell
$p = Start-Process -FilePath .\desktop\bin\Debug\net10.0-windows\MSGuide.Desktop.exe -ArgumentList '--integration-test', '--test-results', 'desktop-integration-results.json' -NoNewWindow -Wait -PassThru
$p.ExitCode
Get-Content .\desktop-integration-results.json
```

`--integration-test` is distinct from `--self-test` and `--capture-test`, and incompatible with either. Exit **0** means all checks passed; **1** means an assertion, capture, environment, API, cleanup, timeout, or report-write failure. `--test-results <path>` is optional and writes compact JSON (also attempted on redirected stdout). The report contains fixed check/stage identifiers, pass/fail, exception type, and a `detail` field containing only the source line for a harness assertion. Other exception messages are suppressed: **no screenshots, raw observations, tokens, transcripts, or service response bodies**. Paths with spaces need quoting in Start-Process arguments. Self-test supports the same report option.

From the repository root, `./scripts/Start-MSGuide.ps1 -IntegrationTest` builds the desktop and owns the temporary demo server/token on port 8765, including cleanup. The harness yields to the WPF dispatcher before showing its demo and awaits `ContentRendered` plus loaded/visible/nonzero layout checks before activation. A failure at `demo-activate` means `DemoWindow.Activate()` returned false; focus is not forced or treated as a pass. A failure at `demo-foreground` means the owned-process/foreground check failed.

Runtime check (2026-09-14): the parent launcher built successfully and reached the healthy demo backend. The demo completed rendering and passed layout/visibility checks, but activation returned false (`demo-activate`, exit 1). This session is blocked on window activation; capture, guidance, overlay, and pause integration checks were not reached. Re-run from an unlocked foreground-capable interactive session; this result is not integration success.

The WPF dispatcher stays running while native capture works. The harness creates exactly one real demo, cancels/drains a native capture, and captures all four states through CaptureService. It checks exact state headings/button labels and valid boxes, sends consented **UIA metadata only** through the real ApiClient, checks matching response IDs and the View logs → Open troubleshooting → Mark resolved → completed sequence. The backend accepts validated PNGs, but the deterministic provider ignores pixels, so this harness omits images.

For each target it shows the real overlay, verifies foreground did not change, physical placement, transparent/noactivate styles and native hit tests, and checks that the real underlying demo button is invokable. Only the test's own WPF button automation peer is invoked; normal guidance never clicks. Dispatcher idle is awaited between transitions instead of fixed sleeps. Finally it moves the owned window to invalidate its snapshot and exercises the real MainWindow pause/cancel clearing and stale-generation guard locally, without showing/loading that companion or sending additional evidence. All owned windows are hidden/closed and evidence disposed on success or failure.

Capture failure is never skipped or substituted with fabricated pixels/metadata. Allow up to four minutes for the run (30-second per-capture budget). Run only while the test demo can retain foreground; locked/headless sessions, focus theft, unsupported/blank captures, and stuck native workers fail honestly. The local pause check is not an end-to-end delayed HTTP race test. Multi-monitor/DPI rendering, chooser disappearance, arbitrary protected windows, hotkeys, speech/microphone, service disconnect/401, and malicious/out-of-order backend responses still require separate manual verification. No complete runtime UX verification is claimed merely by building or passing self-test.
