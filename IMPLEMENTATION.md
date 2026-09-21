# Implementation status

Updated September 21, 2026. This is a **local, single-user desktop MVP with non-executing Guide mode and bounded, locally grounded plan execution**, not an enterprise service or unrestricted agent. Start with [README.md](README.md); historical evidence and remaining live checks are in [docs/VALIDATION.md](docs/VALIDATION.md).

## Implemented path

1. [scripts/Start-MSGuide.ps1](scripts/Start-MSGuide.ps1) builds WPF, starts the loopback backend on port 8765 by default, and gives both children an ephemeral local bearer token. Only the backend inherits the model API key. Server lifetime is tied to desktop/launcher cleanup; no token file is created.
2. [desktop/CompanionWindow.cs](desktop/CompanionWindow.cs) provides the normal Clicky-style shell: a click-through Windows-logo buddy and response bubble follow the cursor at 60 FPS, while the hotkey opens a compact interactive prompt with monitor-edge clamping. [desktop/MainWindow.xaml.cs](desktop/MainWindow.xaml.cs) remains the orchestration and expanded review/approval surface.
3. [desktop/CaptureService.cs](desktop/CaptureService.cs) uses selected-HWND Windows Graphics Capture, without desktop/PrintWindow fallback, and cached, bounded UIA evidence prioritized by actionability. Non-action context cropping does not invalidate a complete controls scan; actual traversal failures remain blocked. Logical control identity is separate from the exact reviewed-state target token. It provides a local preview, not OCR/redaction; PNGs are bounded to 1280 pixels on their longest side and 2,000,000 bytes.
4. [desktop/ApiClient.cs](desktop/ApiClient.cs) calls health, sessions, and guidance only. It rejects non-loopback destinations, disables proxies/redirects, and does not call legacy assist/action routes.
5. [src/main.py](src/main.py) checks the local boundary, session, consent, freshness, body limits, provider output, and target correspondence. It defines **10 application routes**, excluding FastAPI's four generated documentation/schema routes; see [docs/API.md](docs/API.md).
6. [src/guidance.py](src/guidance.py) implements the deterministic built-in demo. [src/model_provider.py](src/model_provider.py) and [src/copilot_provider.py](src/copilot_provider.py) select only reviewed targets; neither provider executes desktop tools. Model setup is [separate from local security mode](docs/MODEL_SETUP.md).
7. [desktop/ScreenTaskSession.cs](desktop/ScreenTaskSession.cs) validates/retains up to 32 steps per plan and executes continuously without eight-action or two-minute checkpoints. Every action is observed; suitable post-action evidence is reused for the next local binding. Page changes within the selected window and plan-limit/observation boundaries automatically capture a fresh screenshot and request a new plan, without repeated approval. Each new page must be completely inspected and identified; no queued old-page target or completion guess is reused. Empty plans do not spin. [desktop/DesktopAction.cs](desktop/DesktopAction.cs) still reacquires/checks each exact target, value, capability and window before invoking. Deferred intents require unique fresh complete matches and deferred writes require empty fields. Missing grounding, unresolved identity and actual permission/info/new-window boundaries stop explicitly. An explicit user-selected window can rebind the retained task only after the old plan is discarded; fresh capture and planning are still required. Per-operation deadlines and the 10,000-decision protocol ceiling remain; unknown and cancelled queues cannot resume.
8. The compact interactive prompt and Details share mode, continuation/reply and Stop handlers. Generic actions bring only the approved window forward from the companion, visibly place the Windows marker/outline on the target, then revalidate before invocation; unrelated foreground changes stop rather than cause background input. The cursor buddy/overlay remain click-through and non-activating. Speech transcript invalidation is separate from hardware shutdown acknowledgement; stopping/unknown input gates new recordings and input changes, and late closure never revives cancelled text. Whisper model-load, processor and inference timings are separate; no speculative factory cache was added.
9. Only the completion bubble expires after five seconds; the marker and retained task details remain. New activity cancels that expiry. Progress, errors, unknown outcomes and required-input messages stay visible until cleared or replaced.

## Implemented safeguards, not enterprise guarantees

- The API accepts loopback clients/local Host values and rejects browser Origin headers. `/v1` and `/admin/` require the configured bearer token before body parsing. All authenticated calls share the local principal; no employee identity or cross-user authorization is established.
- Requests are bounded to 3,000,000 bytes. Observations expire after 60 seconds, with at most five seconds of future clock skew. The desktop additionally checks window identity/bounds and response echo IDs.
- [src/models.py](src/models.py) validates complete plan shapes and boundaries before any first action. Observed references become server-derived logical IDs; future intents are bounded exact descriptions, not invented IDs/selectors. Generic completion remains a model suggestion with `review_required` UI, never independently verified goal success.
- Execution remains desktop-owned: supported UIA invocation, toggling, selection, expansion, bounded full-field replacement and small scrolling only. Password/read-only/value/identity/window checks remain live. No arbitrary typing, dragging, coordinate input, shell or filesystem tool execution is added. Native apps use the explicitly selected HWND/process/class/title as their resource identity. Supported Edge/Chrome windows use the stricter binding between canonical browser-chrome display addresses and the native active document, with capture/lookup scoped to that page. Handoffs require explicit user selection and fresh evidence; partial approved evidence can still support Guide descriptions.
- [src/images.py](src/images.py) uses Pillow to decode/verify bounded PNGs and re-encode pixels without metadata. Valid PNGs are accepted even in demo mode, but deterministic guidance ignores them. Sharing pixels is unnecessary for that demo.
- Manual developer sessions require capture review and approval. A `-Copilot` launch uses its process-lifetime screen-context grant for foreground capture. Prompt or consent changes, cancellation, and supersession invalidate pending work. Native capture runs on a worker with a 30-second waiting budget; it cannot be safely force-aborted. One stuck worker can block later captures until restart.
- Evidence is not deliberately persisted. Bounded rotating diagnostics under `%LOCALAPPDATA%\MSGuide\logs` contain operational metadata only, never screenshots, transcripts, tokens, UI text, prompts, or model output. Clearing references/arrays does not guarantee erasure of all managed/native copies. Remote retention is the configured provider's policy, not controlled here.

## Clicky reference and scope

The MIT-licensed public reference is
[`farzaa/clicky` at `a80fa807`](https://github.com/farzaa/clicky/tree/a80fa80721a8aebe51a170a7780705024ebc6e46).
`CompanionManager.swift` sends a screenshot and conversation to its model;
`OverlayWindow.swift` animates the returned point while preserving cursor
tracking and cancellation. It is a teaching/pointing companion, not an
autonomous action runner, and the repository states that newer changes are
private. MSGuide implements that interaction independently on Windows, retaining
its supported native action path rather than substituting unvalidated pixel
clicks or claiming parity with unavailable code.

## Backend-only sample features

[src/retrieval.py](src/retrieval.py) returns two bundled public sample passages by keyword, not live or permission-trimmed search. `/v1/assist` exposes this sample path; it is not the desktop guidance path.

[src/policy.py](src/policy.py) and [src/actions.py](src/actions.py) permit simulated `view_logs` and `create_work_item` only. Preview → confirm → execute uses stored parameters and expiring single-use grants. Jobs have real in-memory states and cancellation, but **no external effects**. High-risk operations return a refusal/step-up-required result; step-up authentication is not implemented.

Sessions, previews, jobs, and optional audit events are bounded process-local state. Audit is off unless `MSGUIDE_ENABLE_AUDIT=true`, records correlation/time/outcome only, and has no separate administrator identity. Restart loses state; multi-worker operation is unsupported.

## Evidence and gaps

The new offline suites exercise both fake providers/API, whole-plan rejection,
three and 17 steps with one model call, automatic continuation, scope/target drift,
stable effects after label/position changes, compact handlers, and fake-input
stop timeout/late acknowledgement. These are not live-model/app/device acceptance
or native Whisper performance measurements. Older build/test counts and camera/
voice observations in historical runbooks predate these changes.

On September 21 the separate real `browser-e2e` check passed with a live model,
three visible native actions in an owned Edge fixture, fresh post-action evidence
and an independent DOM completion check. This is deliberately narrower than
arbitrary-app acceptance; see the dated validation report.

Do not infer capture, target placement, microphone, mixed-DPI behavior, or end-to-end desktop success from build/unit checks. See [docs/VALIDATION.md](docs/VALIDATION.md) and [PLAN.md](PLAN.md). Python/NuGet locks and CI do not constitute live acceptance. OCR, enterprise access, an internal-data pilot and production deployment remain out of scope.
