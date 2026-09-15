# Guide me / Do it for me: first implementation

September 15, 2026

**Later increment:** [experimental Notepad dual-mode task](NOTEPAD_TASK.md) adds the first external adapter behind an opt-in gate. Its native acceptance remains pending. This document describes the original owned-demo adapter and its evidence, not general external control.

## Product direction versus delivered scope

MSGuide's direction is one assistant that can either teach a task or perform it with permission. This increment implements both modes for one fixed local task: **View logs → Open troubleshooting** in the companion's own Build Center. It stops before Mark resolved and does not claim a repaired build.

This is a scripted adapter with direct access to its own WPF controls, not general screen understanding. It invokes actual local button behavior synchronously through a semantic control method; it does not inject global mouse/keyboard input, use a model to select actions, or automate external applications. The adapter never calls the mock action API.

The original screenshot/UI Automation guidance workflow remains available separately. Its capture limitations are not fixed by this adapter. Task-state checks use the known local fixture, not screenshots, and send nothing to a service or model.

## Try it

1. Start the desktop using [the existing launcher](../scripts/Start-MSGuide.ps1), or open the built desktop executable directly for the offline task. A failed service health check does not prevent this local demo.
2. Select **Open demo**. Return to the companion using the taskbar. Do not minimize the companion during execution; minimizing revokes the task.
3. Choose **Guide me** or **Do it for me (demo only)**.
4. Select **Prepare demo task**. Review the exact task and its 60-second scope, then check task approval.
5. Select **Start approved task**.
   - **Guide:** select the indicated button yourself, then return to **I did it · check**. The adapter verifies the expected transition before advancing.
   - **Control:** the adapter invokes one approved button per 1.2-second dispatcher tick and verifies its state transition. This pacing leaves an opportunity to interrupt between actions; it is not a verification delay.
6. Read the final status: troubleshooting reached, no build repaired, authority revoked.
7. Reset the demo and try the other mode. Resetting an approved/running task invalidates it.

The prompt below the task panel belongs to snapshot guidance; it does not change this fixed plan. Other windows in the capture chooser do not receive control permission. Only the exact DemoWindow instance opened by this companion is eligible—not another window with the same title.

## Interruption and authorization

- Stop task, manual takeover, Pause/clear, mode switch, prompt edits, capture/service operations and window selection changes revoke task authority.
- Unchecking task consent while running stops it. Switching from Guide to Control always requires a fresh plan and approval.
- Moving/resizing/closing the demo, expiry, unexpected content changes, or focus outside the companion/demo stops execution. No attempt is made to steal focus.
- Guide mode cannot call the executor. Control mode accepts only the two named controls in the expected revision of this specific window.
- Each action is synchronous on the UI dispatcher; no future input events are queued. Stop prevents subsequent actions but cannot undo one already completed. The adapter's own actions are short and synchronous; this mechanism is not suitable for arbitrary long-running UI handlers.
- After success, approval is revoked. No automatic retry or resume occurs.
- Local task approval covers its bounded fixture-state checks. It does not authorize screenshots, model uploads, general desktop capture, access requests or cloud operations.

## Verification results

| Check | Result |
| --- | --- |
| .NET desktop build | Passed. |
| Desktop safety self-tests | Passed. |
| Control component harness | Passed: three two-action runs; shared guide/control plan; unapproved, revoked and guide execution blocked; reset/rename/disabled/moved/closed targets rejected; simulated focus loss and expiry rejected. |
| Companion interruption handlers | Passed in component harness: Stop, takeover, consent withdrawal, mode change, Pause and prompt edit revoke authority; a simulated late tick performs no action. |
| Strict interactive control harness | Blocked at foreground-required; no actions executed. Same class of native activation limitation as earlier integration. |
| Full interactive mode selection/approval/overlay experience | Still requires an unlocked, foreground-capable desktop rehearsal. Component tests do not prove this. |

The executable accepts `--control-test --test-results desktop/obj/control-results.json` for native foreground checks and `--control-component-test --test-results desktop/obj/control-component-results.json` for component coverage. The latter uses real owned WPF controls but **explicitly simulated focus and time**. The regular desktop constructor always uses native foreground checks and real time; it has no setting to bypass those checks.

Existing reports live under the ignored desktop build-output directory. Older screenshot-capture/foreground failures remain relevant; see [VALIDATION.md](VALIDATION.md). Backend code was not changed and backend tests were not rerun for this increment.

## Next implementation boundary

Before enabling external control: validate interactive Stop/takeover and grounding, then add one explicitly supported external-app adapter. That requires fresh target identity, action-specific authorization, consequential-action confirmation, sensitive-field exclusions, prompt-injection resistance and reliable post-action evidence. Neither a trusted button label nor model confidence alone authorizes a real action.

The four stories in [DEMO_SCENARIOS.md](DEMO_SCENARIOS.md) remain proposed guide-first workflows. This increment supplies the first dual-mode fixture, not those integrations. The Excel playbook's no-automation instructions still describe its guide-mode version.
