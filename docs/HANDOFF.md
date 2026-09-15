# Implementation handoff

## September 15 native acceptance update — latest

Fixed overlay foreground stealing traced to WPF's DPI-change resize calling `SetWindowPos` without `SWP_NOACTIVATE`. The border-only overlay handles the source DPI event, retaining its initial render scale while explicit native positioning owns physical bounds. The ineffective `WM_WINDOWPOSCHANGING` workaround was removed. Text/control windows must not use this policy; border thickness across DPI changes still needs visual rehearsal.

Build, safety/Notepad policy tests, simulated-focus demo components, strict native demo Control (three two-action runs), and capture/API (18 checks) pass. Full integration passed three post-fix runs; the final two include zero activation events, synchronous/deferred foreground preservation, reposition/hide/re-show, exact bounds, hit-through, cancellation and pause checks (19 checks). No target reactivation, capture fallback or relaxed foreground guard was used. These are owned-window native tests, not real Notepad or manual UX acceptance.

Real Notepad Guide/Control and interruption acceptance await explicit user participation; experimental writes remain disabled by default. No live model was called. Historical blank captures remain unexplained. Backend tests were not rerun in this desktop-only increment; earlier 150-test evidence below is historical. README whitespace was fixed. Work is local and uncommitted; nothing was pushed. See [VALIDATION.md](VALIDATION.md).

## September 15 experimental Notepad increment

Added a single-draft Notepad adapter with shared Guide/Control UI, exact target/text approval, bounded local verification, single-tab checks and interruption safeguards. External writes default off pending native acceptance; synthetic testing requires `MSGUIDE_ENABLE_EXPERIMENTAL_NOTEPAD_CONTROL=1`. Build and component tests pass; the user was unavailable for native click-based validation. Selected-window capture passed repeated default-rendering diagnostics but remained intermittent in the software-rendering comparison. No capture/focus guard was weakened. See [NOTEPAD_TASK.md](NOTEPAD_TASK.md) for verified evidence, remaining risks and the manual acceptance checklist. Backend and model code are unchanged.

## September 15 dual-mode increment

The desktop now offers Guide me and Do it for me for a bounded local Build Center task. Real semantic button invocation is restricted to its exact owned window and stops at troubleshooting; no external control or backend changes. Build, safety tests and focus-simulated component tests pass. Strict native control testing remains blocked at foreground activation. See [DUAL_MODE.md](DUAL_MODE.md) for usage, consent, interruption tests and remaining gates. Earlier snapshot evidence below is unchanged.

## Delivered

- Native WPF desktop companion with hotkey, text, local speech, explicit capture review/consent, fresh-step controls, and nonactivating highlights.
- Synthetic build workflow plus deterministic screen-evidence guidance.
- Authenticated single-user loopback API with validated sessions, observations, bounded PNG processing, and mock-only action lifecycle.
- Optional explicitly approved model endpoint adapter; no service or model selected and no real model call made.
- One-command launcher: [scripts/Start-MSGuide.ps1](../scripts/Start-MSGuide.ps1).
- Backend, provider, image, desktop safety, and real-window test harnesses.

## Earlier verification — superseded by latest update above

- 150 backend tests passed; installed dependencies have no conflicts.
- .NET 10 build and desktop safety self-tests passed.
- Capture/API harness passed once across all four demo states (18 checks). Final reruns failed on blank window pixels, safely rejected before upload.
- Foreground integration failed because the rendered demo could not activate. Full interactive overlay and microphone validation remains outstanding.
- Live model integration needs an approved endpoint/model and secure environment credentials; transport tests use mocks only.
- README has a cosmetic multiple-trailing-blank-lines Markdown diagnostic that resisted editor cleanup; no executable-code diagnostics were reported.

## Next verification on the user's desktop

Use an unlocked Windows desktop and PowerShell 7. Start the launcher from the foreground terminal, then use Open demo → Capture / review → approve → Send → click yourself → Check next step. See [README.md](../README.md) for setup. If capture is blank, discard it; do not enable remote image sharing or bypass the guard. Re-run the capture and integration harnesses as described in [docs/VALIDATION.md](VALIDATION.md).

This is an implemented local MVP, not completed enterprise software. No employee identity, permission-aware internal search, pixel OCR/redaction, real external actions, or production deployment is included. The latest validation guide supersedes earlier passing-capture summaries in other documents.
