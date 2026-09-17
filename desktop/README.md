# MSGuide Desktop — native guide-only MVP

WPF / .NET 10 Windows companion. Local dictation uses **Whisper.net 1.9.1**,
its CPU runtime (including Windows ARM64), and **NAudio.WinMM 2.2.1** for
microphone capture. **System.Speech 10.0.0** supplies playback and synthetic
test audio, not the interactive dictation engine. A .NET 10 SDK is required to
build; Python 3.11 and PowerShell 7 are required by the root launcher. No cloud
provisioning or Developer Mode is needed.

## Build and start

Build with `dotnet build desktop/MSGuide.Desktop.csproj` from the repository root using the installed .NET 10 SDK. Run `dotnet run --project desktop/MSGuide.Desktop.csproj` from the authenticated launcher's environment, or launch `desktop/bin/Debug/net10.0-windows/MSGuide.Desktop.exe` from that environment. The parent launcher owns starting the backend and generating the ephemeral token.

- `MSGUIDE_LOCAL_TOKEN`: required for `/v1` calls; inherited from the launcher, never embedded or persisted by this client.
- `MSGUIDE_API_URL`: default `http://127.0.0.1:8000`. Only a literal loopback HTTP origin or `localhost` is accepted; localhost is pinned to 127.0.0.1. Proxies and redirects are disabled to prevent forwarding the token.
- `MSGUIDE_HOTKEY`: optional, default `Ctrl+Alt+M`; examples `Ctrl+Shift+G` or `Alt+Shift+M`. A registration conflict is reported. Without a working hotkey, dismiss minimizes to the taskbar instead of making the app unreachable.
- Expected backend: `GET /health` with status `ok`, version `0.2.0`, mode `demo`/`model`; authenticated `POST /v1/sessions` and `POST /v1/guidance`. No legacy assist/action routes are called.

## Try the supported workflow

1. Start the local backend and desktop through the parent launcher. Read the mode label: **DEMO** means deterministic sample guidance, not AI; **MODEL** means the backend-configured provider, not a claim of enterprise authorization.
2. Click **Open demo**. A separate **MSGuide Demo** window shows a synthetic build dashboard; only **View logs** is initially available.
3. Invoke the companion with the hotkey, type “Help me find the build error”, select **MSGuide Demo**, and click **Capture / review**.
4. Inspect the actual image (expand full-size inspection if needed) and UI Automation text/element metadata. Pixel sharing defaults **off**. Check consent, optionally enable image sharing for a vision provider, then click **Send approved snapshot**. No capture is uploaded without this click.
5. Switch to the demo to see the nonactivating outline. Click the indicated button yourself. **View logs** reveals “Build failed: exit code 1” and **Open troubleshooting**; that reveals “Check compiler errors and missing dependencies” and **Mark resolved**; the last step shows “Issue resolved”. Old workflow buttons are removed. All changes are synthetic and local.
6. **Check next step** starts a fresh capture/review, never a blind resend. Repeat approval at each step. **Reset demo** restarts the sandbox.
7. Select a **Microphone**, then **Start microphone** / **Stop & transcribe** for local Whisper dictation. Recording stops at 30 seconds and is transcribed after microphone closure. Input levels and low-volume diagnostics are visible. Review/edit the transcript before **Ask MSGuide**; submission is disabled until transcription finishes. Use **Cancel transcription** while local processing is active. **Sound input settings** opens Windows' volume/mute controls without changing them. **Speak response** is optional local playback.
8. **Pause / clear**, **Dismiss**, Escape, minimize, or Exit cancel work, clear snapshot references/bytes and transcript, stop microphone/speech, and hide highlights. Already transmitted data cannot be recalled. A title-bar close exits the application.

## Privacy and support boundaries

- Manual selected-HWND `PrintWindow(PW_RENDERFULLCONTENT)` only. **Normal mode has no CopyFromScreen, desktop capture fallback, periodic capture, automatic upload, injected clicks, or keystrokes.** The timer only checks bounds/window identity and snapshot age. The explicit integration-test and capture-test harnesses invoke only buttons belonging to their own synthetic DemoWindow instance.
- Full window bounds use physical coordinates, matching the bitmap and normalized UIA boxes. PNGs are limited to 1600 pixels on the longest side and 2,000,000 bytes. Large physical allocations are rejected. Captures stay in memory; the application writes no screenshots, transcripts, tokens, or request logs to disk. .NET/WPF may retain temporary managed/native copies until collection; this is not a forensic memory-erasure guarantee.
- UI Automation names are collected with bounded traversal, not pixel OCR. Offscreen/password subtrees and disabled target controls are excluded. **This is not image redaction**: screenshot pixels and other accessible text can still contain passwords or sensitive information. There is no redaction editor. Do not approve sensitive content; discard it. A backend model may process approved content remotely even though the client connects only to loopback.
- Some GPU, elevated, protected, minimized, or unresponsive windows cannot be captured. Blank/uniform images are rejected heuristically (not guaranteed detection). UIA can be unavailable or incomplete. Inspect the preview rather than assuming capture success means all pixels are valid.
- PrintWindow and UI Automation are synchronous native/provider calls on a background worker. The UI stops waiting after 30 seconds or cancellation (a real capture can take 10 seconds); a stuck native call cannot be safely terminated. At most one native capture is outstanding, late data is discarded, and a stuck provider may require restarting the client. Process isolation is deferred.
- Snapshot TTL is 60 seconds. Window moves, resizes, minimization, disappearance/title change, approval changes during a request, prompt edits, or supersession invalidate work and clear old answers/citations/playback. Echo IDs and response schema are checked. Targets below 0.8 confidence, with invalid normalized boxes, or without a matching reviewed UIA label/box are not highlighted. The timer cannot detect every in-window content change: after acting, request a fresh step. Focus loss removes a displayed outline; it does not silently restore an old one.
- Native overlay placement uses physical desktop coordinates and a PerMonitorV2 manifest; WPF draws the border in local DIPs. Negative origins and scale math have executable checks, but mixed-DPI monitor rendering/straddling windows still require runtime verification. Capture exclusion via `SetWindowDisplayAffinity` is best effort, not a security guarantee.
- Citation URLs never open automatically; only a user click opens an HTTPS source. Other schemes display as non-clickable text. HTTPS does not imply that a source is trusted.
- No tray dependency, enterprise sign-in, authorized enterprise retrieval, always-on voice, OCR engine, arbitrary desktop action execution, or deployment is included.

## Runnable checks and verification

Run `desktop/bin/Debug/net10.0-windows/MSGuide.Desktop.exe --self-test` (or `dotnet run --project desktop/MSGuide.Desktop.csproj -- --self-test`). This exits 0 on success, 1 on failure, without showing a window or connecting to the backend. Checks cover loopback URL rejection, unsafe citation schemes, freshness boundaries, response echo mismatch, malformed target boxes, and physical target mapping at 100/125/150/200% scale with a negative desktop origin.

Install the local English model with
`scripts\Install-MSGuideSpeechModel.ps1 -AcceptDownload` after approving the
466 MiB download. Its verified model file stays under
`%LOCALAPPDATA%\MSGuide\models`, never in the repository. Normal startup never
downloads or substitutes another speech model automatically.

Speech lifecycle self-tests use a fake input and never open a microphone.
Set `MSGUIDE_WHISPER_SYNTHETIC_TEST=1` for real local Whisper transcription of
synthetic speech and silence. These use memory only, with no speaker output,
microphone input, audio file, or remote call. The older
`MSGUIDE_SPEECH_SYNTHETIC_TEST=1` checks the legacy Windows test adapter only;
it is not proof of Whisper accuracy.

### Capture-only runtime check (parent starts backend)

`--capture-test` is a separate, narrower test, mutually exclusive with `--integration-test` and `--self-test`. Use the parent's existing `MSGUIDE_API_URL` and `MSGUIDE_LOCAL_TOKEN`; health must report `ok`, `demo`, `0.2.0`. The harness never starts a backend or generates credentials. Parent launcher support is separate work.

It renders its own actual DemoWindow with `ShowActivated = false`, awaits `ContentRendered`, and passes only that instance's HWND/PID to the real CaptureService. It does not call Activate or require foreground ownership. PrintWindow/UIA can capture a background window; an unavailable desktop, unsupported/blank capture, or missing UIA still fails, never skips or fabricates evidence.

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
