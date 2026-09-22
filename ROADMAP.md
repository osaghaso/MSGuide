# Remaining acceptance gates

The product direction now includes **Guide me** and permission-scoped **Do it for me**. The local dual-mode fixture, an [experimental Notepad adapter](docs/NOTEPAD_TASK.md), and a first generic selected-window action slice are implemented. An explicit Copilot launch authorizes the generic slice to perform one stable-ID-reacquired UIA `invoke`, `toggle`, `select`, `expand`, or `collapse` per response without another prompt; the model selects reviewed evidence but has no execution tool. Next: pass user-assisted foreground/Stop/takeover tests and broaden only from measured app-specific evidence. Typing, scrolling, coordinate input, ambient observation, and general desktop control remain unimplemented.

The current implementation is summarized in [SUMMARY.md](SUMMARY.md); detailed progress is in [PLAN.md](PLAN.md). This roadmap is not a production commitment or a claim of completed UX validation.

## 1. Unblock the synthetic desktop run

Rerun [the integration harness](docs/VALIDATION.md) from an unlocked, foreground-capable Windows desktop. Investigate `demo-activate` without bypassing its assertion or replacing captures with fixtures. Record the report and verify capture, UIA state transitions, target placement, and cancellation before claiming an end-to-end demo.

Then manually check hotkeys, preview/consent, changing windows, disconnects, accessibility, speech, protected windows, and mixed-DPI monitors. A passing harness alone would not cover all of these.

## 2. Validate an explicitly approved live model

Use [docs/MODEL_SETUP.md](docs/MODEL_SETUP.md), synthetic/public screens, and explicit per-snapshot consent. Measure next-step usefulness, target correctness, refusals, stale evidence, latency, and completion uncertainty. Verify screenshot-grounded guidance separately from UIA-only guidance. Mocked transport tests do not satisfy this gate.

Broader application support, pixel OCR, and redaction remain unimplemented. Add them only for a declared supported scenario, with tests for sensitive pixels and inaccessible UIA. Never present metadata stripping as redaction.

## 3. Establish reproducible installation and support

Validate setup on a clean Windows machine and record exact resolved dependencies. Current requirements pin direct dependencies only; a full transitive lock remains to be implemented. Define supported OS/speech configurations, runtime limitations, and recovery instructions from measured results.

## 4. Gate enterprise data and actions separately

Before any internal-data pilot, obtain approved identity/data handling, implement server-validated enterprise authentication and permission-aware retrieval, and test access isolation. None is supplied by `MSGUIDE_MODE=demo` or model credentials.

Keep enterprise/downstream actions disabled until authorization, least privilege, exact preview/confirmation binding, idempotency, cancellation semantics, and audit requirements are implemented and reviewed. The backend action API still only simulates effects and the desktop does not use it. The local UIA slice is a separate per-action boundary and is not evidence that enterprise action requirements are complete.

## 5. Consider a controlled pilot only after the gates pass

Security, privacy, accessibility, dependency review, operational ownership, retention/deletion, support/rollback, and recorded approval remain open. Shared persistence is required before considering multi-worker/multi-instance use. **Phase 7 is not complete. No production deployment is provided.**

Always-on listening, automatic screen streaming, arbitrary desktop control, unrestricted background agents, and deployment automation remain outside the current scope.
