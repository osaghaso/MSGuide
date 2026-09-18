# Implementation status

Updated September 15, 2026. This is a **local, single-user desktop MVP with guide mode and narrowly approved UI Automation actions**, not an enterprise service or unrestricted agent. Start with [README.md](README.md); evidence and remaining checks are in [docs/VALIDATION.md](docs/VALIDATION.md).

## Implemented path

1. [scripts/Start-MSGuide.ps1](scripts/Start-MSGuide.ps1) builds WPF, starts the loopback backend on port 8765 by default, and gives both children an ephemeral local bearer token. Only the backend inherits the model API key. Server lifetime is tied to desktop/launcher cleanup; no token file is created.
2. [desktop/CompanionWindow.cs](desktop/CompanionWindow.cs) provides the normal Clicky-style shell: a click-through Windows-logo buddy and response bubble follow the cursor at 60 FPS, while the hotkey opens a compact interactive prompt with monitor-edge clamping. [desktop/MainWindow.xaml.cs](desktop/MainWindow.xaml.cs) remains the orchestration and expanded review/approval surface.
3. [desktop/CaptureService.cs](desktop/CaptureService.cs) captures the selected HWND with `PrintWindow` and collects bounded UI Automation names/boxes, stable target IDs, and supported semantic actions. It provides a real local preview, not OCR or redaction. Following Clicky's inference-size pattern, PNGs are bounded to 1280 pixels on their longest side and 2,000,000 bytes.
4. [desktop/ApiClient.cs](desktop/ApiClient.cs) calls health, sessions, and guidance only. It rejects non-loopback destinations, disables proxies/redirects, and does not call legacy assist/action routes.
5. [src/main.py](src/main.py) checks the local boundary, session, consent, freshness, body limits, provider output, and target correspondence. It defines **10 application routes**, excluding FastAPI's four generated documentation/schema routes; see [docs/API.md](docs/API.md).
6. [src/guidance.py](src/guidance.py) implements the deterministic built-in demo. [src/model_provider.py](src/model_provider.py) and [src/copilot_provider.py](src/copilot_provider.py) select only reviewed targets; neither provider executes desktop tools. Model setup is [separate from local security mode](docs/MODEL_SETUP.md).
7. [desktop/OverlayWindow.cs](desktop/OverlayWindow.cs) draws a nonactivating, click-through target outline with a Windows-logo marker. [desktop/DesktopAction.cs](desktop/DesktopAction.cs) can reacquire the exact reviewed UIA target and perform one `invoke`, `toggle`, `select`, `expand`, or `collapse` action. An explicit Copilot launch grants session automation, so one freshly grounded action per response runs without another prompt. It rejects changed window identity/bounds, target identity, availability, action, or toggle state and never retries an unknown outcome.

## Implemented safeguards, not enterprise guarantees

- The API accepts loopback clients/local Host values and rejects browser Origin headers. `/v1` and `/admin/` require the configured bearer token before body parsing. All authenticated calls share the local principal; no employee identity or cross-user authorization is established.
- Requests are bounded to 3,000,000 bytes. Observations expire after 60 seconds, with at most five seconds of future clock skew. The desktop additionally checks window identity/bounds and response echo IDs.
- [src/models.py](src/models.py) validates normalized target boxes and confidence. Targets must match reviewed UIA evidence; model output selects an existing element index, never arbitrary coordinates. Model completion claims become clarification with a verification warning.
- Generic desktop action authority remains desktop-owned and single-use. The model receives the reviewed action name as bounded metadata but cannot execute it directly. Typing, scrolling, dragging, coordinate input, shell/filesystem access, and autonomous observation are not implemented.
- [src/images.py](src/images.py) uses Pillow to decode/verify bounded PNGs and re-encode pixels without metadata. Valid PNGs are accepted even in demo mode, but deterministic guidance ignores them. Sharing pixels is unnecessary for that demo.
- Manual developer sessions require capture review and approval. A `-Copilot` launch uses its process-lifetime screen-context grant for foreground capture. Prompt or consent changes, cancellation, and supersession invalidate pending work. Native capture runs on a worker with a 30-second waiting budget; it cannot be safely force-aborted. One stuck worker can block later captures until restart.
- Evidence is not deliberately persisted. Bounded rotating diagnostics under `%LOCALAPPDATA%\MSGuide\logs` contain operational metadata only, never screenshots, transcripts, tokens, UI text, prompts, or model output. Clearing references/arrays does not guarantee erasure of all managed/native copies. Remote retention is the configured provider's policy, not controlled here.

## Backend-only sample features

[src/retrieval.py](src/retrieval.py) returns two bundled public sample passages by keyword, not live or permission-trimmed search. `/v1/assist` exposes this sample path; it is not the desktop guidance path.

[src/policy.py](src/policy.py) and [src/actions.py](src/actions.py) permit simulated `view_logs` and `create_work_item` only. Preview → confirm → execute uses stored parameters and expiring single-use grants. Jobs have real in-memory states and cancellation, but **no external effects**. High-risk operations return a refusal/step-up-required result; step-up authentication is not implemented.

Sessions, previews, jobs, and optional audit events are bounded process-local state. Audit is off unless `MSGUIDE_ENABLE_AUDIT=true`, records correlation/time/outcome only, and has no separate administrator identity. Restart loses state; multi-worker operation is unsupported.

## Evidence and gaps

The current automated run reports **206 pytest passes**, a successful .NET build, and a passing desktop self-test. Provider tests use synthetic evidence and mocked transport, not an approved live model. The real harness previously failed when `demo.Activate()` returned false after visible layout/rendering; foreground restrictions are only an unconfirmed explanation.

Do not infer capture, target placement, microphone, mixed-DPI behavior, or end-to-end desktop success from build/unit checks. See [docs/VALIDATION.md](docs/VALIDATION.md) and the unchecked milestones in [PLAN.md](PLAN.md). No OCR, enterprise access, internal-data pilot, full dependency lock, or production deployment is complete.
