# Validation and known blockers

## Automatic navigation and completion feedback — September 21, 2026

The production task-loop regressions now cover multiple page/resource changes
within one selected window without approval pauses: fresh screenshots and plans,
retained task/history, no old-page target reuse, and final-page completion review.
Input, permission, manual/new-window handoffs, failed grounding, cancellation and
unknown outcomes remain stops. The isolated desktop build and all 42 self-test
groups passed. Both model providers and the API passed 312 targeted tests,
including historical old-page plans alongside newly scoped observations.

The focused native feedback check passed: completion remains visible after
4.2 seconds and expires after five seconds, including while the prompt is open.
The marker and task details remain, expired feedback is not restored, and newer
progress/error messages do not expire with the old completion timer. Foreground
was unchanged.

The browser fixture now changes its URL fragment after each button. Its updated
live-model/native-action run verified the owned page identity but stopped at
`browser-awaiting-user-foreground` before planning or invoking any action.
Foreground checks were not bypassed. Thus the automatic navigation changes have
production-loop coverage, but this updated live-browser scenario is not yet a
passing end-to-end acceptance result. The successful earlier run below used the
original same-page fixture.

## Live browser execution — September 21, 2026

**The full `browser-e2e` path passed** on the owned local Edge fixture after the
desktop became interactive. The actual production MainWindow captured the page,
requested a live Copilot plan, visibly presented and invoked all three buttons
in order, observed each action, and reached completion review. An independent
Playwright read confirmed `["one", "two", "three"]` and the visible
`All three steps finished` result. Playwright did not click the task buttons.

Cold browser checks also exposed and fixed renderer accessibility initialization:
the first inspection could have an address but no page root. Bounded passive
inspection of native document wrappers allowed `RootWebArea` to appear on the
next pass, without changing browser settings or using another inspector first.
Page content is never accepted as the browser's address control.

The Windows marker now takes a short cancellable flight before each native
action, following the public Clicky interaction reference. Its system pointer,
foreground and exact-target checks remain separate from the visual effect.
The final backend suite passed 355 tests and the desktop/probe Release build
and self-tests passed without warnings.

This is a real model/browser/native-input acceptance result for the synthetic
fixture, not a claim of arbitrary websites, privileged cloud changes, or feature
parity with Clicky's private releases. A separate run refused to start when the
test lacked foreground; another safely discarded a response after focus changed.
Those safety stops are not converted into successful executions.

## Browser identity repair — September 20, 2026

The reported zero-action `resource_changed` stop was reproduced in an isolated
real Edge window. Its address control omitted the protocol prefix, and browser
chrome exposed auxiliary `Document` nodes alongside the page. The old check
therefore returned no resource identity before the first planned action.

The corrected scope combines the canonical displayed address with the native
active `RootWebArea` identity. Capture and target lookup are confined to that
page. Missing initial identity now reports `resource_unverified`, rather than
claiming navigation occurred. A stray plain-text prefix in `src/guidance.py`
was also removed because it prevented a fresh backend from importing.

- Release build and desktop self-tests passed; the backend suite passed 355 tests.
- A real local Edge fixture passed identity/capture checks, obtained a three-step
  plan from the configured live model, and reacquired every native target.
  This was the explicitly read-only `browser-plan-readonly` path: synthetic UIA
  metadata only, no screenshots or input.
- **At the time of this run, full visible browser acceptance was blocked.**
  Windows did not provide foreground access; the computer-use engine also
  refused to risk focus disturbance. No foreground workaround or Azure action
  was performed. This is not a successful click-through acceptance result.

See [the browser acceptance commands](../desktop/README.md#browser-acceptance)
for the interactive test. It must be run on an unlocked desktop with the owned
test browser foreground. Earlier native/fixture results below remain historical
evidence, not proof of the changed browser path.

## Recorded evidence — September 15, 2026

| Check | Result | Scope |
| --- | --- | --- |
| .NET desktop build and editor diagnostics | Passed | No new compile/editor errors. |
| Desktop safety and Notepad policy | 9 check groups passed | Fake Notepad editor, not external-app acceptance. |
| Demo Control components | 13 checks passed | Explicit simulated focus/time and real companion interruption handlers. |
| Strict native demo Control | 12 checks passed, including three two-action runs | Actual foreground checks, owned demo only. |
| Four-state capture/API | 18 checks passed | Actual selected-HWND pixels and UIA, deterministic loopback API. |
| Full native integration | Three post-fix passes; final two have 19 checks | Zero overlay activation, foreground preservation, reposition/hide/re-show, exact bounds, hit-through, cancellation and pause. |
| Real Notepad and live model | Pending | No native user consent available; no live provider called. |

The overlay failure was traced to WPF's DPI resize path calling `SetWindowPos` without `SWP_NOACTIVATE`. [OverlayWindow](../desktop/OverlayWindow.cs) handles the source DPI event to suppress that automatic resize for its border-only visual; explicit native positioning retains physical target bounds. Its render scale stays at the initial scale, so mixed-DPI border thickness/visual quality still needs manual inspection. This policy must not be reused for text or interactive controls. No focus restoration or assertion bypass is used. [IntegrationTests](../desktop/IntegrationTests.cs) now also rejects transient activation events.

Windows sometimes denies initial foreground activation; native tests now use the existing bounded real-activation wait helper, never simulated focus. Historical blank captures remain unexplained: these recent successful runs do not establish universal capture reliability. Backend source was unchanged and its full suite was **not rerun** in this increment; the 150-test result below is earlier evidence. All recent changes remain local, uncommitted and unpushed.

## Recorded evidence — September 14, 2026

Historical results below are superseded by the September 15 table where applicable. Distinguish deterministic backend, capture, and foreground checks from a live-model evaluation.

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

[requirements.txt](../requirements.txt) and [requirements-dev.txt](../requirements-dev.txt)
retain direct dependency intent. Portable `requirements.lock.txt` (runtime) and
`requirements-dev.lock.txt` (runtime plus tests) record the tested project closure
and wheel hashes without workstation-specific mirror URLs, targeting Windows x64
CPython 3.11.9. Follow [setup](../README.md): create the venv, then install with
`.\venv\Scripts\python -m pip install --require-hashes --only-binary=:all: -r requirements-dev.lock.txt`.
Ordinary installation needs no experimental lock support. ARM64 Windows can use
x64 Python. NuGet restores use the checked-in package locks and `--locked-mode`.
Regenerate locks intentionally after direct manifest changes using pip 26.2.1
and `scripts\lock_python_dependencies.py`; do not weaken TLS validation. Clean locked-install
and CI validation are separate from the historical working-tree results above;
the existence of lockfiles alone is not proof of either.

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

The harness captures its **own** real DemoWindow, inspects four UIA states, sends **UIA metadata only** through the real API, validates response identities/targets, tests overlay hit-through/positioning/zero activation, and invalidates evidence on movement. Snapshot guidance never clicks; the separate explicitly approved local Control task can invoke its fixed demo plan. The harness also checks capture cancellation and local pause clearing, but the pause check is not a delayed-HTTP end-to-end race test.

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
