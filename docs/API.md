# Local API reference

API version **0.2.0**, defined in [src/main.py](../src/main.py) with contracts in [src/models.py](../src/models.py). Start through [the launcher](../README.md), normally at loopback port **8765**. This is a single-worker local API, not a deployment interface.

## Boundary and route inventory

There are **10 application routes** below. FastAPI also generates `/openapi.json`, `/docs`, `/docs/oauth2-redirect`, and `/redoc` (14 route entries in total). Documentation availability does not authorize browser API access: requests carrying any Origin header are rejected.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/health` | Status, version, and provider mode; no bearer required. |
| POST | `/v1/sessions` | Create a one-hour local session; no request body required. |
| POST | `/v1/guidance` | Consented fresh observation to one validated guidance result. |
| POST | `/v1/assist` | Legacy bundled-public-sample answers, not desktop guidance. |
| POST | `/v1/actions/preview` | Validate/store a mock tool and parameters; five-minute expiry. |
| POST | `/v1/actions/{previewId}/confirm` | Issue one short-lived, single-use execution grant. |
| POST | `/v1/actions/{previewId}/execute` | Consume the grant and start a simulated job. |
| GET | `/v1/jobs/{jobId}` | Read actual in-memory mock job state. |
| POST | `/v1/jobs/{jobId}/cancel` | Cancel a pending/running simulation; no external rollback. |
| GET | `/admin/audit-log` | Optional bounded local audit; disabled by default. |

All `/v1` and `/admin/` requests require `Authorization: Bearer <launcher-generated token>`. Do not use an employee token or a made-up mock token. The launcher passes the ephemeral credential directly to its children; do not paste it into chat or save it in a file.

Clients must be loopback, Host must be local, and Origin must be absent. All authenticated callers share owner `local`; ownership/expiry checks are not enterprise user isolation. The whole request body is limited to **3,000,000 bytes**. Unknown contract properties are rejected.

## Desktop request flow

The desktop calls only health, sessions, and guidance. Health reports `status: ok`, `version: 0.2.0`, and `mode: demo` or `model` according to the guidance provider. `MSGUIDE_MODE` still remains `demo` for local-user security in either case.

Session creation returns `sessionId` and `expiresAt`. Guidance requires:

| Field | Constraint |
| --- | --- |
| `sessionId` | Existing, unexpired local session. |
| `prompt` | 1–4000 characters. |
| `consent` | Explicit boolean `true`; otherwise HTTP 403. |
| `observation.id`, `windowId` | 1–128 character identifiers using letters, digits, underscore, dot, colon, or hyphen. |
| `observation.application` | 1–256 characters; desktop uses the window title. |
| `observation.capturedAt` | Timestamp with explicit UTC offset; no older than 60 seconds or more than five seconds in the future. |
| `observation.width`, `height` | Integers 1–16384; images have stricter limits below. |
| `observation.ocrText` | Required text, at most 16000 characters; desktop supplies UIA names, not pixel OCR. |
| `observation.elements` | Required list, at most 200 entries: `role`, `label`, `box`, `confidence`; optional strict evidence is described below. |
| `observation.imageBase64` | Optional raw canonical base64 PNG, not a data-URL string. Omit unless pixel sharing was approved. |
| `observation.automationComplete` | Strict boolean; an incomplete controls inspection cannot authorize generic execution. |
| `observation.resourceId` | Optional opaque local resource-scope ID. Required for queued desktop execution; absence still permits descriptive guidance. |
| `planSegments` | Strict boolean, default `false` for legacy clients. The desktop sends `true`; a missing plan then fails explicitly rather than silently returning one click. Mutually exclusive with camera recovery. |
| `cameraRecovery` | Optional deterministic Teams camera-recovery request. Omit for legacy/demo guidance. |
| `task` | Optional bounded generic continuation context; mutually exclusive with `cameraRecovery`. Not execution authority or a server job. |

Boxes are normalized `[x, y, width, height]`, nonempty and entirely inside `[0,1]`. Labels are 1–256 characters, roles 1–64; element confidence is 0–1. PNGs must be single-frame, no more than 1600 pixels per side, match the declared dimensions, and fit within 2,000,000 bytes before and after sanitization (base64 cap 2,666,668 characters). Pillow verifies and re-encodes pixels without metadata. This is not pixel redaction.

Each element may additionally provide a reviewed-state `targetId`, stable logical
`controlId`, `automationId`, `frameworkId`, `processId`, `isEnabled`, `isOffscreen`,
and `toggleState` (`off`, `on`, `indeterminate`). Non-null `targetId` and
`controlId` values must each be unique in an observation. The desktop derives
logical identity from the selected window, provider PID and nonempty runtime ID,
separately from the label/box-bound reviewed token. Missing/ambiguous runtime IDs
do not authorize actions. The observation may also supply `rootProcessId` and
`teamsCameraSessionState`. Descendant provider PIDs need not equal the selected
root PID; selected-HWND rooting is mandatory. These fields are evidence, not grants.

Elements can declare `action` (`invoke`, `toggle`, `select`, `expand`, `collapse`,
`set_value`, `scroll`), `targetable`, `isPassword`, `isReadOnly`, `valueHash`,
`valueLength`, `isSelected`, `scrollDirections`, and horizontal/vertical scroll
percentages. Executable targets require a stable ID, enabled/targetable true,
offscreen false, and confidence at least 0.8. Writable value targets additionally
require non-password/read-only-false evidence and a SHA-256 value digest/length
(at most 1000); raw existing field values are not transmitted. Scroll directions
come from supported current UIA state. Non-actionable text remains observation
context, never an executable target.

Guidance returns `instruction`, `status` (`next_step`, `clarification`, `needs_input`,
`blocked`, `completion_candidate`, or deterministic `completed`), optional `target`
and `remainingWork`, citations, mode, correlation ID, and observation/window IDs.
A target is allowed only for `next_step` and must match fresh observed
identity/label/box/state/action evidence. `set_value` requires an explicit `value`
of 0-1000 characters and the same `valueHash`; `scroll` requires one allowlisted
`scrollDirection`. Other actions forbid these inputs. The desktop checks the
exact control and input again immediately before invoking.

`task` contains a UUID `taskId`, strictly increasing decision `step` (1-10000),
local status, at most 16 ordered prior history entries, and `remainingWork` /
`userInput` strings of at most 1000 characters each. Each entry binds a step,
before/optional-after observation ID, target ID, label, action, and outcome
(`effect_observed`, `screen_changed`, `no_progress`, `unknown`, `not_invoked`).
The server echoes `taskId` and `step` separately in its response. Providers receive
this as untrusted context, not a claim of success or authority to reuse a target.
The desktop owns the bounded in-memory task and its verification/continuation UI.
Continuation context can additionally contain `plan`, `planCursor` (0 through the
segment length), and `replanReason` (at most 500 characters). This retained plan is
untrusted model context; old target tokens are not execution authority.

## Structured plan segments

With `planSegments: true`, generic guidance returns `status: "next_step"`,
`target: null`, and a `plan`. The canonical shape is:

```json
{
  "planId": "00000000-0000-0000-0000-000000000001",
  "windowId": "window-1",
  "resourceId": "resource-synthetic",
  "steps": [
    {
      "kind": "action",
      "instruction": "Open the currently observed menu.",
      "controlId": "control-observed",
      "intent": {"role": "button", "label": "Menu", "action": "invoke"}
    },
    {
      "kind": "action",
      "instruction": "Select the next uniquely matching control.",
      "intent": {"role": "button", "label": "Details", "action": "invoke"}
    }
  ],
  "boundary": {
    "kind": "needs_input",
    "reason": "The next part needs information from the user.",
    "needed": "Provide the requested resource or clarification."
  }
}
```

There are at most **32 steps per response**, not per execution run. The desktop
has no eight-action or two-minute execution checkpoint. Every step has a nonempty instruction of at most 500
characters. `kind` is `action` or a terminal `manual` handoff. Manual steps have
no target or action parameters and cannot precede more executable steps.
Boundary kinds are `completion_candidate`, `resource`, `needs_input`,
`permission`, `observation`, `unsupported`, and `plan_limit`. `reason` is 1-500
characters; `needed` is at most 1000 and mandatory/nonblank except for completion
suggestions. A 32-step segment cannot claim completion; it must expose a boundary.
After observed progress, `plan_limit` and `observation` automatically request a
fresh plan on the same approved, completely inspectable resource. Other boundary
kinds still stop for review. An empty plan never causes automatic replanning;
the existing 10,000-decision protocol ceiling remains.

Action intents use exact `role`, `label`, supported `action`, and optional exact
`automationId` / `frameworkId`. Toggles require the expected `toggleState` on/off;
selection requires `isSelected: false`. `value` and a `valueHash` are required
only for `set_value`; `scrollDirection` is required only for `scroll`. A deferred
write (no `controlId`) must use the SHA-256 of an empty value, so it cannot replace
unexpected nonempty text. Known-control steps preserve the server-derived
reviewed value hash. Password/read-only/availability/value checks run again live.

Model input is deliberately different: each action selects one approved
`targetId` (Copilot) / `targetIndex`, or supplies a deferred `intent`. The server
derives canonical `controlId` and preconditions from observed references.
Invented opaque IDs, unknown fields, unsupported operations, malformed later
steps, scope mismatch, and incompatible inputs reject the whole plan before its
first action. Deferred intents are descriptions, never cached targets: the
desktop resolves each against fresh complete evidence, requiring exactly one
actionable match and a unique logical identity before creating a fresh target token.

The desktop reuses suitable post-action evidence for the next local binding.
Routine expected changes do not cause another model call. It retains remaining
steps across batch checkpoints; missing/changed targets and resource boundaries
pause for explicit review/replanning. New windows are not selected, permissions
are not granted, and external resources are not fetched automatically. Generic
document trees without a proven file/site identity require handoff; scope/caption
changes cannot authorize the rest of an old plan. Guide mode remains non-executing,
including when approved partial text/images can support a descriptive plan.

Legacy clients can omit `planSegments`; single-target responses remain supported.
The native capture/integration harness explicitly requests that legacy shape.
Camera recovery is independent and does not request/upload generic plan segments.

Camera targets additionally return an opaque `targetId` plus the observed process, automation, framework, enabled, offscreen, and toggle evidence. A supplied capture-local ID is echoed; otherwise the server generates one. In both cases the server binds it to the session, observation identity/time, element index, box, label, and state before returning it. A moved or mismatched target is invalid. Older non-camera providers may continue to return targets without these additive fields.

The default provider uses MSGuide Demo UIA evidence; images are accepted but
ignored. Copilot selects allowlisted target/citation IDs; the OpenAI-compatible
provider selects a UIA index. Neither invents coordinates or executes desktop
tools. Model completion becomes `completion_candidate`, not verified completion;
the generic desktop loop displays `review_required`, unlike the dedicated local
camera/demo verifiers. Invalid/absent provider results fail explicitly.

Evidence still expires at 60 seconds. SDK/API/desktop waits are capped at 50/52/54
seconds and shortened by evidence age, reserving 10/8/6 seconds respectively.
The OpenAI-compatible provider uses a 50-second cap for plan output and retains
its ten-second cap for legacy single-step requests. Old evidence
with insufficient headroom fails before inference; results are freshness-checked
again. HTTP disconnect cancels owned provider work. Copilot explicitly aborts
before bounded detach; cancelling the SDK wait alone is not sufficient. There
are no automatic guidance resends or unknown-action retries.

## Deterministic Teams camera recovery

The desktop opts in on the existing `/v1/guidance` route and must select an explicit evidence profile:

```json
{
  "cameraRecovery": {
    "profile": "teams-camera-recovery-win11-24h2-en-US-fixture-v1"
  }
}
```

This path bypasses the configured guidance provider. The server derives and retains only bounded, volatile session progress from fresh observations. `teams-camera-recovery-win11-24h2-en-US-fixture-v1` is a synthetic fixture fallback, not a live Windows Settings UIA support claim. Its predicates rank exact automation IDs above exact fixture labels and also require the configured application, role, framework, and confidence.

The optional response `cameraRecovery` contains `profile`, `evidenceBasis` (`fixture` or `liveProbe`), `fixtureSupported`, `settingsUiaProven`, `rawPixelEvidenceUsed` (always `false`), optional pinned `settingsLaunchUri`, `state`, bounded `evidence`, `permissionState`, and `verificationRequired`. States are `start`, `teams_prejoin_observed`, `camera_block_confirmed`, `teams_settings_menu_open`, `teams_devices_open`, `camera_settings_open`, `system_camera_settings_unverified`, `system_camera_settings_uninspectable`, `applicable_permission_off`, `user_action_required`, `applicable_permission_on`, `camera_reinitialization_required`, `return_to_teams`, `camera_ready_verified`, `unsupported`, `admin_managed`, and `ambiguous`.

The live-probe profile is:

```json
{
  "cameraRecovery": {
    "profile": "teams-camera-recovery-new-teams-uia-probe-20260916-v1"
  }
}
```

It recognizes only probe-backed New Teams navigation evidence: `more-options-header`, the Settings item, the Devices `TabItem`, Devices-page markers `AudioSettings`/`VideoSettings`, and the originally observed `open_camera_settings`. The September 16 target-machine probe selected Teams PID 4444 while accessible WebView descendants reported PID 16836, so the backend deliberately does not impose same-process filtering after the desktop has rooted capture to the selected HWND.

That v1 profile preserves the first probe's fail-closed result: zero UIA descendants from the then-active Settings windows. It never targets Windows Settings or accepts completion.

The current pinned-machine profile is:

```json
{
  "cameraRecovery": {
    "profile": "teams-camera-recovery-pinned-20260916-v2"
  }
}
```

After closing the specific existing `SystemSettings` process and relaunching `ms-settings:privacy-webcam`, UIA exposed the correct Camera page. The URI alone is not evidence: with the old Settings instance alive it landed on Settings Home. The backend verifies the page from all three exact IDs before targeting anything:

- `SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch`;
- `SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch`;
- `MSTeams_8wekyb3d8bbwe_ToggleSwitch`.

Both global toggles must be explicitly on. The packaged Teams toggle must be enabled, onscreen, and uniquely matched. The pinned golden path prepares that individual toggle off while `teamsCameraSessionState` is explicitly `notInitialized`, highlights it for the user to turn on, then observes it on while Teams is still not initialized. Already-active, already-on, wrong-page, global-off, disabled, offscreen, ambiguous, stale, or changed evidence fails closed. `SystemSettings_CapabilityAccess_Camera_ClassicGlobal_ToggleSwitch` may be present offscreen but is not the selected packaged-app target.

The 20-read stability census found `VideoSettings` and `MSTeams_8wekyb3d8bbwe_ToggleSwitch` in 20/20 reads, each completing in 119–232 ms. System global, app global, and Teams permission states were consistently on; the Teams toggle was enabled and visible. Provider PIDs consistently differed from top-level PIDs. This evidence is scoped only to the current open pages and pinned machine.

`open_camera_settings` was not realized in any of those 20 reads. The pinned profile therefore does not return that UIA target. On the Teams Devices state it returns untargeted `next_step` guidance with `settingsLaunchUri: "ms-settings:privacy-webcam"`. The desktop may use that hard-coded pinned URI, but must then capture and verify all exact Camera page IDs; URI dispatch is never proof of landing.

The reversible behavior probe showed why ordering is required: changing the packaged permission while Teams Devices already had a live preview caused no Camera `ComboBox`, error-signal, or preview-contrast change after four seconds, and restoring permission also caused no immediate UI change. An already-open camera session can survive the permission change. The backend therefore never treats a live toggle transition or unchanged preview as failure or recovery evidence.

After permission restoration, the pinned profile returns `camera_reinitialization_required` until a newer Teams observation reports `teamsCameraSessionState: "reinitialized"`. Final verification additionally requires `reinitializationMethod` on `localCameraReady`, with one of `prejoinReopened`, `teamsRelaunched`, or `cameraDeviceReinitialized`. It then requires the probe-backed Teams Devices surface and Camera `ComboBox`. Permission-on alone remains incomplete. Settings and Teams provider PIDs may differ from their top-level processes; selected-HWND rooting, not PID equality, is the capture boundary.

On this machine, `PrintWindow` flag 0 returned black while flag 2 returned rendered New Teams and Camera Settings content, including the live preview. Flag 2 is undocumented and remains a desktop-measured, pinned-machine fallback, never a backend invariant. Raw `imageBase64` is sanitized in memory, ignored by the deterministic engine, never used to advance state, and never persisted by this service. Preview pixels may be personal; the desktop must avoid saving them. The earlier blank `CaptureTest` and demo-activation `IntegrationTest` failure are not treated as successful end-to-end validation.

Permission-on is not completion. In the synthetic fixture and pinned-machine profiles, the server returns `completed` only for `camera_ready_verified`, after the profile-specific off-to-on permission sequence and matching Teams evidence with this explicit verifier payload:

```json
{
  "cameraRecovery": {
    "profile": "teams-camera-recovery-win11-24h2-en-US-fixture-v1",
    "verification": {
      "kind": "localCameraReady",
      "source": "desktopLocalCameraVerifier",
      "evidenceId": "capture-local-verifier-id",
      "sessionId": "same-session-id",
      "observationId": "same-observation-id",
      "windowId": "same-window-id",
      "capturedAt": "same-capture-time-window",
      "cameraActive": true,
      "framesObserved": 2,
      "reinitializationMethod": "prejoinReopened"
    }
  }
}
```

The verifier must be fresh, within five seconds of the observation, bound to the same session/observation/window, report an active local camera, and include 2–120 observed frames. `reinitializationMethod` is required by the pinned profile and optional for the synthetic fixture. The desktop owns this local verifier; the backend does not call Teams, camera, settings, or external APIs. Missing/conflicting verifier evidence stops at verification required. Disabled/managed controls, already-on or wrong-cause flows, ambiguous candidates, unsupported surfaces, reused/older observations, and target mismatches fail closed without actions or writes.

## Legacy samples and simulated actions

`/v1/assist` requires `sessionId` and `prompt`; optional context must reference the same session, be fresh, and have `sensitivity: public`. `responseModes` only describes requested output; it does not invoke desktop speech or overlays. Retrieval uses two bundled keyword-matched sample passages, not an enterprise search service.

Preview accepts `{tool, parameters}`. `view_logs` accepts optional `application` (default MSGuide Demo); `create_work_item` requires `title` and accepts `description`. Both are simulations requiring confirmation. High-risk tools return HTTP 403 with `STEP_UP_REQUIRED`; unknown/blocked tools return `DENIED`. No step-up flow or external execution exists.

Confirm returns `grantToken`, `expiresIn`, `expiresAt`, and `mock`. Execute accepts only `{grantToken}` and uses the stored preview parameters. Grants cannot be replayed. Job states are `pending`, `running`, `success`, `failed`, or `cancelled`; results explicitly indicate simulation. Job records expire after ten minutes; at most 16 mock jobs may be active.

These 250 ms mock jobs do not invoke UIA and are not generic desktop task
records. No server queue/executor was added; desktop progress travels on the
optional guidance `task` contract only.

`MSGUIDE_ENABLE_AUDIT=true` enables the audit route with the same local bearer boundary, not administrator RBAC. `limit` is 1–1000 (default 100); `count` is the current buffer size, while `events` contains the requested tail. Only correlation ID, timestamp, and outcome are recorded. All state is volatile and bounded; restart clears it.

## Common failures

| HTTP | Meaning |
| --- | --- |
| 400 / 413 | Invalid local Host/content length, or oversized body. |
| 401 / 503 | Wrong/missing bearer, or local authentication not configured. |
| 403 | Non-loopback/browser-origin request, missing consent, nonpublic legacy context, or denied action/grant. |
| 404 / 410 | Unknown/not-owned resource, disabled audit, or expired resource. |
| 409 | Already confirmed/consumed preview or grant. |
| 422 | Invalid request, stale observation, or invalid image. |
| 429 | Local state capacity or active mock-job limit reached. |
| 499 | Guidance client disconnected; owned inference was cancelled. |
| 502 / 504 | Invalid/failed provider result or guidance timeout. |

Capture again and explicitly approve after a desktop error; there is no automatic resend. See [validation](VALIDATION.md) for tests and [model setup](MODEL_SETUP.md) for opt-in remote processing.
