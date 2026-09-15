# Desktop-first implementation plan

Updated September 14, 2026. **Implemented local components; foreground end-to-end validation blocked.** [SUMMARY.md](SUMMARY.md) gives the status overview; [IMPLEMENTATION.md](IMPLEMENTATION.md) maps the source; [docs/VALIDATION.md](docs/VALIDATION.md) records evidence.

Checked items mean the named implementation/check exists with the stated evidence—not that the whole phase passed runtime acceptance. Use synthetic/public data only. No production deployment is planned by this document.

## Phase 1 — Local backend stabilization

- [x] Enforce loopback/Host/Origin and ephemeral-token boundaries; support only local `MSGUIDE_MODE=demo` security mode.
- [x] Validate sessions, consent, freshness, bounded bodies, targets, previews, grants, and job state.
- [x] Use direct ASGI tests rather than Starlette's legacy TestClient/httpx constructor path.
- [x] Pin direct requirements; latest normal pytest run passed 150 tests and pip check was clean.
- [x] Replace obsolete enterprise/production claims in root documentation with implemented behavior.
- [ ] Produce a complete transitive dependency lock and verify installation on a clean machine.

## Phase 2 — Desktop shell

- [x] Implement WPF/.NET 10 companion, configurable hotkey, taskbar fallback, typed input, mode labels, and service/session handling.
- [x] Implement manual review/consent, explicit Send, fresh Check, pause/dismiss, and safe citation opening.
- [x] Implement nonactivating overlay and physical-coordinate checks; .NET build and non-UI safety self-test pass.
- [x] Provide a real-window integration harness and built-in synthetic demo.
- [ ] Pass interactive foreground/capture/overlay integration; latest run stops at `demo-activate`.
- [ ] Verify mixed-DPI/multi-monitor rendering, keyboard/screen-reader use, hotkey conflicts, focus behavior, and disconnect recovery manually.

Tray integration and desktop action controls are not part of the current shell.

## Phase 3 — Screen guidance

- [x] Implement selected-HWND capture with actual preview; no automatic desktop capture/upload or normal-mode input injection.
- [x] Collect bounded UIA names/boxes, exclude password/offscreen subtrees, and require per-snapshot approval.
- [x] Validate bounded PNGs with Pillow and strip metadata before remote processing; pixels remain unredacted.
- [x] Implement deterministic built-in demo guidance and validated next-step/clarification/completed contracts, with unit tests.
- [x] Implement optional explicitly configured model transport and mocked-transport/image-validation tests.
- [x] Implement freshness, response echo/target checks, supersession, cancellation, and fresh Check behavior.
- [ ] Make native capture reliably repeatable: one 18-check capture/API run passed, but latest reruns fail on blank captured pixels. Preserve fail-closed behavior and investigate on an unlocked interactive desktop.
- [ ] Verify real capture → guidance → overlay → user progress end to end; foreground activation currently blocks the harness.
- [ ] Verify live-model screenshot grounding and target accuracy on approved synthetic/public screens.
- [ ] Implement and evaluate broader application support, pixel OCR, or redaction where required.

The existing `ocrText` field carries UIA names in the desktop path, not OCR output. Model transport implementation alone does not complete the screen-guidance milestone.

## Phase 4 — Local voice

- [x] Implement installed Windows recognizer dictation, editable transcript, and optional speech playback.
- [x] Implement click-to-toggle microphone, 30-second auto-stop, interruption, and typed fallback.
- [ ] Verify microphone/voice availability, language selection, denial/error behavior, interruption, and accessibility on an interactive desktop.

No wake word, always-listening mode, or remote audio service is included.

## Phase 5 — Enterprise identity and knowledge

- [ ] Obtain integration/data-processing approvals before using internal content.
- [ ] Implement server-validated employee identity, permissions, and authoritative compliance signals.
- [ ] Replace bundled sample lookup with permission-aware retrieval and ingestion/refresh processes.
- [ ] Evaluate grounding, citations, access isolation, and prompt-injection handling with approved data.

Local bearer authentication and model API credentials do not satisfy these requirements.

## Phase 6 — Safe external actions

- [x] Implement backend-only mock preview → confirmation → execution, expiring single-use grants, bounded jobs, and cancellation; covered by backend tests.
- [x] Refuse high-risk/unknown tools and keep all external effects disabled.
- [ ] Implement an approved read-only connector with downstream authorization.
- [ ] Add any real write connector only after authorization, exact parameter binding, idempotency, audit, and cancellation reviews/tests.
- [ ] Add desktop action UI only if separately required; capture consent never grants action authority.

Mock jobs are not real connectors or durable background tasks.

## Phase 7 — Controlled pilot (not complete)

- [x] Document local setup, current API/provider configuration, limitations, and the failed integration run.
- [ ] Complete runtime acceptance and live-model evidence before a product demonstration is marked successful.
- [ ] Define retention/deletion, operational ownership, monitoring, support, rollback, and kill-switch procedures.
- [ ] Complete security, privacy, accessibility, and dependency reviews.
- [ ] Implement appropriate shared state before multi-worker/multi-instance operation.
- [ ] Record approval for pilot users, sources, actions, costs, and data processing; measure outcomes.

## Next acceptance demonstration

First pass the deterministic demo on an unlocked desktop without skipping activation/capture failures. Then separately validate an approved live-model loop: preview → consent → Send → one justified step → user click → fresh Check, including interruption, stale evidence, unsupported screens, and window movement. Record actual results, not just successful builds.

See [ROADMAP.md](ROADMAP.md) for the remaining gates. Broader screenshot/live-model grounding, OCR, enterprise access, and pilot readiness remain open.
