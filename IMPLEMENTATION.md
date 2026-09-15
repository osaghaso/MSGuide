# Implementation status

Updated September 14, 2026. This is a **local, single-user, guide-only desktop MVP**, not an enterprise service. Start with [README.md](README.md); evidence and remaining checks are in [docs/VALIDATION.md](docs/VALIDATION.md).

## Implemented path

1. [scripts/Start-MSGuide.ps1](scripts/Start-MSGuide.ps1) builds WPF, starts the loopback backend on port 8765 by default, and gives both children an ephemeral local bearer token. Only the backend inherits the model API key. Server lifetime is tied to desktop/launcher cleanup; no token file is created.
2. [desktop/MainWindow.xaml.cs](desktop/MainWindow.xaml.cs) manages the hotkey, window selection, editable prompt, capture review, consent, explicit Send, fresh Check, and cancellation.
3. [desktop/CaptureService.cs](desktop/CaptureService.cs) captures the selected HWND with `PrintWindow` and collects bounded UI Automation names/boxes. It provides a real local preview, not OCR or redaction. PNGs are bounded to 1600 pixels per side and 2,000,000 bytes.
4. [desktop/ApiClient.cs](desktop/ApiClient.cs) calls health, sessions, and guidance only. It rejects non-loopback destinations, disables proxies/redirects, and does not call legacy assist/action routes.
5. [src/main.py](src/main.py) checks the local boundary, session, consent, freshness, body limits, provider output, and target correspondence. It defines **10 application routes**, excluding FastAPI's four generated documentation/schema routes; see [docs/API.md](docs/API.md).
6. [src/guidance.py](src/guidance.py) implements the deterministic built-in demo. [src/model_provider.py](src/model_provider.py) implements the opt-in OpenAI-compatible transport; neither provider can execute tools. Model setup is [separate from local security mode](docs/MODEL_SETUP.md).
7. [desktop/OverlayWindow.cs](desktop/OverlayWindow.cs) draws a nonactivating target outline. [desktop/SpeechService.cs](desktop/SpeechService.cs) supplies installed Windows speech recognition/playback. The user clicks; Check begins another review cycle.

## Implemented safeguards, not enterprise guarantees

- The API accepts loopback clients/local Host values and rejects browser Origin headers. `/v1` and `/admin/` require the configured bearer token before body parsing. All authenticated calls share the local principal; no employee identity or cross-user authorization is established.
- Requests are bounded to 3,000,000 bytes. Observations expire after 60 seconds, with at most five seconds of future clock skew. The desktop additionally checks window identity/bounds and response echo IDs.
- [src/models.py](src/models.py) validates normalized target boxes and confidence. Targets must match reviewed UIA evidence; model output selects an existing element index, never arbitrary coordinates. Model completion claims become clarification with a verification warning.
- [src/images.py](src/images.py) uses Pillow to decode/verify bounded PNGs and re-encode pixels without metadata. Valid PNGs are accepted even in demo mode, but deterministic guidance ignores them. Sharing pixels is unnecessary for that demo.
- Capture/upload is manual. Prompt or approval changes, cancellation, and supersession invalidate pending work. Native capture runs on a worker with a 30-second waiting budget; it cannot be safely force-aborted. One stuck worker can block later captures until restart.
- Evidence is not deliberately persisted. Clearing references/arrays does not guarantee erasure of all managed/native copies. Remote retention is the configured provider's policy, not controlled here.

## Backend-only sample features

[src/retrieval.py](src/retrieval.py) returns two bundled public sample passages by keyword, not live or permission-trimmed search. `/v1/assist` exposes this sample path; it is not the desktop guidance path.

[src/policy.py](src/policy.py) and [src/actions.py](src/actions.py) permit simulated `view_logs` and `create_work_item` only. Preview → confirm → execute uses stored parameters and expiring single-use grants. Jobs have real in-memory states and cancellation, but **no external effects**. High-risk operations return a refusal/step-up-required result; step-up authentication is not implemented.

Sessions, previews, jobs, and optional audit events are bounded process-local state. Audit is off unless `MSGUIDE_ENABLE_AUDIT=true`, records correlation/time/outcome only, and has no separate administrator identity. Restart loses state; multi-worker operation is unsupported.

## Evidence and gaps

The parent validation run reports **149 pytest passes**, a clean dependency check, successful .NET build, and successful desktop self-test. Provider tests use synthetic evidence and mocked transport, not an approved live model. The real harness failed when `demo.Activate()` returned false after visible layout/rendering; foreground restrictions are only an unconfirmed explanation.

Do not infer capture, target placement, microphone, mixed-DPI behavior, or end-to-end desktop success from build/unit checks. See [docs/VALIDATION.md](docs/VALIDATION.md) and the unchecked milestones in [PLAN.md](PLAN.md). No OCR, enterprise access, internal-data pilot, full dependency lock, or production deployment is complete.
