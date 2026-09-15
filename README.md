# MSGuide desktop MVP

A Windows WPF companion for reviewing a selected window and receiving one guidance step at a time. **You perform every click.** The default provider uses deterministic rules for the built-in **MSGuide Demo** only; it is not AI. An explicitly configured [optional model provider](docs/MODEL_SETUP.md) can process approved synthetic/public evidence.

**Status, September 14, 2026:** 149 backend tests, .NET build, desktop self-test, and real window capture/API tests pass. A separate foreground/overlay integration test stops at window activation. Full interactive UX, microphone operation, and live model quality remain **unverified**. See [validation evidence](docs/VALIDATION.md).

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

## Try the built-in demo

1. Click **Open demo**, then invoke MSGuide with **Ctrl+Alt+M**. The hotkey is configurable through `MSGUIDE_HOTKEY`; use the taskbar if registration fails.
2. Enter “Help me find the build error” **before capture**, and select **MSGuide Demo**.
3. Click **Capture / review**. Inspect the actual image and UI Automation text/boxes, including full-size image inspection. Nothing has been uploaded.
4. Leave screenshot sharing off for the default demo. Check explicit consent, then click **Send approved snapshot**. Consent alone does not send anything.
5. Switch to the demo and click the indicated control yourself: **View logs → Open troubleshooting → Mark resolved**.
6. After each click, use **Check next step** for a **fresh capture and review**, approve again, and send. It is not an automatic observation loop. The final demo screen says **Issue resolved**.
7. Use **Pause / clear**, **Dismiss**, Escape, minimize, or Exit to cancel work and clear the current evidence/transcript. Already transmitted data cannot be recalled.

Editing the prompt, changing the selected window, or moving/resizing the captured window invalidates the snapshot. Snapshots expire after 60 seconds. In-window content changes are not all detected: always request a fresh Check after acting.

## Voice

**Start microphone / Stop microphone** is local click-to-toggle dictation, auto-stopping after 30 seconds—not hold-to-talk or a wake word. Review/edit recognized text before capture; it is never automatically sent. **Speak response** uses local playback. Recognition depends on installed Windows speech recognizers, language support, and a usable default microphone; playback needs an installed voice. Type instead if these are unavailable. **Stop speech** also cancels pending guidance.

## Privacy and limits

- Manual selected-window capture only; no whole-desktop fallback, periodic capture, or normal-mode input injection.
- **UI Automation names only, not pixel OCR. No pixel redaction or redaction editor.** Excluding password controls from UIA does not sanitize screenshot pixels or all accessible text. Discard sensitive captures.
- Screenshot upload is opt-in per snapshot. Pillow validates PNGs and strips metadata before remote processing; it does not remove sensitive pixels. The default backend makes no remote model calls.
- Capture data is held in memory and cleared/disposed on completion or cancellation, not deliberately saved by the application. This is not guaranteed forensic memory erasure or a promise about a remote provider's retention.
- Protected, elevated, GPU-rendered, minimized, blank, or unresponsive windows may fail capture. A stuck native capture may require restarting MSGuide.
- No enterprise authentication, permission-aware search, real external actions, production deployment, or approved internal-data pilot. Use synthetic/public data only.

## Documentation

- [SUMMARY.md](SUMMARY.md): current status at a glance.
- [IMPLEMENTATION.md](IMPLEMENTATION.md): implemented components and boundaries.
- [docs/API.md](docs/API.md): current routes and contracts.
- [docs/MODEL_SETUP.md](docs/MODEL_SETUP.md): explicit provider configuration and data sharing.
- [docs/VALIDATION.md](docs/VALIDATION.md): commands, evidence, blockers, and documentation conflicts.
- [PLAN.md](PLAN.md) and [ROADMAP.md](ROADMAP.md): evidence-based progress and remaining gates.
- [desktop/README.md](desktop/README.md): desktop behavior and platform limitations.







