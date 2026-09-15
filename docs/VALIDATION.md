# Validation and known blockers

## Recorded evidence — September 14, 2026

These results were recorded during implementation; distinguish deterministic backend, capture, and foreground checks from a live-model evaluation.

**Latest result:** 150 backend tests pass and dependency checks are clean. Desktop builds and self-tests pass. The capture test passed once (18 checks), but the latest three reruns failed at `capture-0`; the last two diagnostics identified `blank`: Windows returned pixels rejected as blank/protected/unsupported. Native capture is therefore **not reliably verified** in this session. The stricter integration test remains blocked on activation. Keep the blank-image guard and selected-window boundary; do not substitute whole-desktop capture.

| Check | Result | What it does not prove |
| --- | --- | --- |
| Normal pytest run | **150 passed**, using installed pytest 8.4.2, pytest-asyncio 1.4.0, and Pillow 12.1.1. | Windows UI behavior or live model quality. |
| pip check | **Clean** in the installed environment. | Clean-machine installation or a reproducible full dependency lock. |
| .NET 10 desktop build | **Passed**. | Runtime capture, focus, overlay, or speech. |
| Desktop `--self-test` | **Passed**; [report](../desktop/obj/self-test-results.json) has `passed: true`. | Real windows, API traffic, or end-to-end UX. |
| Launcher + `--capture-test` | Initial **18-check pass**, latest runs **failed** with `blank`; [latest report](../desktop/obj/capture-results.json). | Reliable native capture, foreground activation, overlay interaction, speech, or live-model quality. |
| Launcher + real integration harness | **Failed** at `demo-activate`; [report](../desktop/obj/integration-results.json) has `passed: false`. | Capture, guidance, overlay, and pause checks were not reached. |

The integration run reached a healthy demo API and a rendered, visible, nonzero demo layout. Then `demo.Activate()` returned false. An environmental foreground restriction is **unconfirmed**, not an established root cause. Do not claim desktop UX runtime success or silently skip activation to obtain a pass.

The separate capture test initially passed real selected-HWND capture, UIA evidence extraction, all three API-returned targets, demo completion, stale bounds detection, and evidence disposal. Later blank-image failures mean that success is not consistently reproducible. It deliberately makes no foreground/overlay claims; the original integration assertions remain intact.

## Run backend and non-UI checks

Complete [setup](../README.md) first. From the project root, without activating the environment:

```powershell
.\venv\Scripts\python -m pytest
.\venv\Scripts\python -m pip check
dotnet build .\desktop\MSGuide.Desktop.csproj
$p = Start-Process -FilePath .\desktop\bin\Debug\net10.0-windows\MSGuide.Desktop.exe -ArgumentList '--self-test', '--test-results', 'desktop/obj/self-test-results.json' -Wait -PassThru
$p.ExitCode
Get-Content .\desktop\obj\self-test-results.json
```

The executable is a Windows GUI application, so use its exit code and JSON report rather than expecting terminal stdout. Self-test exits 0 for success and 1 for failure. It checks loopback URLs, citation schemes, freshness, response echoes, target boxes, and physical-coordinate math without opening windows or connecting to a service.

[tests/local_client.py](../tests/local_client.py) wraps `httpx.ASGITransport` directly and runs application lifespan. Tests do not use Starlette's legacy TestClient/httpx constructor path, avoiding that compatibility issue without requiring a downgrade. Provider tests in [tests/test_model_provider.py](../tests/test_model_provider.py) use mocked transport and synthetic images; they are not live-model evaluations.

[requirements.txt](../requirements.txt) and [requirements-dev.txt](../requirements-dev.txt) pin direct requirements only. A complete transitive lock and clean-machine reproducibility validation remain unimplemented. This workspace has **no Git repository**; do not describe these checks as a commit/branch/PR validation.

## Run the real desktop harness

Use **PowerShell 7** on an **unlocked interactive Windows 10/11 desktop** where the demo can retain foreground. Do not run headlessly, while locked, or while another application is stealing focus. Close the normal MSGuide session first or choose a different port.

```powershell
.\scripts\Start-MSGuide.ps1 -IntegrationTest
Get-Content .\desktop\obj\integration-results.json
```

Optional rerun against an existing current build on another free port:

```powershell
.\scripts\Start-MSGuide.ps1 -IntegrationTest -SkipBuild -Port 8766
```

The launcher builds unless skipped, starts its own demo API/token, forces deterministic guidance, and writes the harness report to [desktop/obj/integration-results.json](../desktop/obj/integration-results.json). It cleans up its owned server when the desktop exits or launch fails. The default port is 8765; the direct desktop/API fallback port 8000 is not the launcher default.

Allow up to four minutes. Expected success is desktop exit 0 and report `passed: true`. Failure produces desktop exit 1 and a launcher error; inspect the JSON `stage`, `failure`, and completed `checks`, and verify the report belongs to this run. If startup fails before the harness runs, an old report may remain.

The harness is designed to capture its **own** real DemoWindow, inspect four UIA states, send **UIA metadata only** through the real API, validate response identities/targets, test overlay hit-through/positioning, and invalidate evidence on movement. Only this test harness invokes its own synthetic buttons; normal guidance never clicks. It also checks capture cancellation and local pause clearing, but the pause check is not a delayed-HTTP end-to-end race test.

At `demo-activate`, activation returned false; at `demo-foreground`, the foreground/owned-process check failed. Investigate the actual failing stage. Never substitute fabricated pixels/metadata or suppress failed assertions. A future passing report would still not establish broad Windows application support or live-model grounding.

Reports contain check identifiers, stage, exception type, and an assertion source-line detail—not screenshots, raw observations, transcripts, tokens, or service bodies.

## Manual checks still required

- [ ] Complete the preview → explicit consent → Send → user click → fresh Check loop and verify no automatic uploads or input injection.
- [ ] Verify hotkey/dismiss/taskbar behavior, keyboard/screen-reader accessibility, disconnect/401 handling, and cancellation/revocation during a request.
- [ ] Verify multiple monitors, negative origins, mixed DPI, moving/straddling windows, focus changes, and click-through behavior.
- [ ] Verify blank/protected/elevated/unresponsive windows fail safely; inspect actual preview quality.
- [ ] Verify installed recognizer/voice/microphone limitations, editable dictation, 30-second auto-stop, and interruption.
- [ ] Validate an approved live model separately with synthetic/public text and opt-in images; measure grounding, uncertainty, latency, and unsupported-screen behavior.

## Run the separate capture/API check

```powershell
.\scripts\Start-MSGuide.ps1 -CaptureTest
Get-Content .\desktop\obj\capture-results.json
```

This rendered-window test uses the same real capture and HTTP paths but does not request foreground. Its harness invokes only its own synthetic demo buttons. It does not replace the stricter integration test above.

The launcher in [README.md](../README.md) is the supported setup/start path; the client's direct port 8000 fallback differs from launcher port 8765. A .NET 10 SDK is required. Valid PNGs are accepted in demo mode too, but deterministic guidance ignores their pixels.
