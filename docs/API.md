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
| `cameraRecovery` | Optional deterministic Teams camera-recovery request. Omit for legacy/demo guidance. |

Boxes are normalized `[x, y, width, height]`, nonempty and entirely inside `[0,1]`. Labels are 1–256 characters, roles 1–64; element confidence is 0–1. PNGs must be single-frame, no more than 1600 pixels per side, match the declared dimensions, and fit within 2,000,000 bytes before and after sanitization (base64 cap 2,666,668 characters). Pillow verifies and re-encodes pixels without metadata. This is not pixel redaction.

Each element may additionally provide a capture-local opaque `targetId`, `automationId`, `frameworkId`, `processId`, `isEnabled`, and `toggleState` (`off`, `on`, or `indeterminate`); the observation may provide `rootProcessId`. Strings, integers, and booleans are strict, unknown properties remain rejected, and non-null element `targetId` values must be unique within the observation. A tree remains rooted to the selected `windowId`, but descendant `processId` is evidence only and is not required to equal `rootProcessId`: New Teams WebView descendants can belong to a different process than the selected top-level HWND. These fields are never authority.

Guidance returns `instruction`, `status` (`next_step`, `clarification`, `completed`), optional `target`, `citations`, `mode`, `correlationId`, and echoed `observationId`/`windowId`. A target is allowed only for `next_step`, must have confidence at least 0.8, and must match an observed label/box with sufficient confidence. The client checks freshness and correspondence again before displaying an outline.

Camera targets additionally return an opaque `targetId` plus the observed automation/framework/enabled/toggle evidence. A supplied capture-local ID is echoed; otherwise the server generates one. In both cases the server binds it to the session, observation identity/time, element index, box, label, and state before returning it. A moved or mismatched target is invalid. Older non-camera providers may continue to return targets without these additive fields.

The default provider uses only MSGuide Demo UIA evidence; valid images are accepted but ignored. Other applications or ambiguous targets produce clarification. The optional model selects a UIA index; it cannot invent target coordinates or return tools/citations. Its completion output is downgraded to clarification for user verification. Guidance times out after ten seconds; invalid/provider-failed results are not silently replaced with demo output.

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

The optional response `cameraRecovery` contains `profile`, `evidenceBasis` (`fixture` or `liveProbe`), `fixtureSupported`, `settingsUiaProven` (currently always `false`), `state`, bounded `evidence`, `permissionState`, and `verificationRequired`. States are `start`, `teams_prejoin_observed`, `camera_block_confirmed`, `teams_settings_menu_open`, `teams_devices_open`, `camera_settings_open`, `system_camera_settings_uninspectable`, `applicable_permission_off`, `user_action_required`, `applicable_permission_on`, `return_to_teams`, `camera_ready_verified`, `unsupported`, `admin_managed`, and `ambiguous`.

The live-probe profile is:

```json
{
  "cameraRecovery": {
    "profile": "teams-camera-recovery-new-teams-uia-probe-20260916-v1"
  }
}
```

It recognizes only probe-backed New Teams navigation evidence: `more-options-header`, the Settings item, the Devices `TabItem`, Devices-page markers `AudioSettings`/`VideoSettings`, and `open_camera_settings`. The September 16 target-machine probe selected Teams PID 4444 while accessible WebView descendants reported PID 16836, so the backend deliberately does not impose same-process filtering after the desktop has rooted capture to the selected HWND.

The same probe found zero UIA descendants from both the SystemSettings `CoreWindow` and `ApplicationFrameWindow`, and exact global UIA searches found no Camera access toggles. Therefore the live profile returns `system_camera_settings_uninspectable`, never a Windows Settings target, permission-on claim, or completion. `imageBase64` remains ignored by this engine and does not become visual proof. A future private local visual verifier must be separately and strictly modeled; until then only the explicitly named synthetic fixture can exercise the permission-toggle sequence. The probe's existing `CaptureTest` was blank and `IntegrationTest` failed demo activation, so these findings do not constitute end-to-end desktop validation.

Permission-on is not completion. In the synthetic fixture profile, the server returns `completed` only for `camera_ready_verified`, after this session first observed the configured Teams block, then an enabled applicable permission off, then that permission on, then a matching Teams camera-on observation with this explicit verifier payload:

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
      "framesObserved": 2
    }
  }
}
```

The verifier must be fresh, within five seconds of the observation, bound to the same session/observation/window, report an active local camera, and include 2–120 observed frames. The desktop owns this local verifier; the backend does not call Teams, camera, settings, or external APIs. Missing/conflicting verifier evidence stops at verification required. Disabled/managed controls, already-on or wrong-cause flows, ambiguous candidates, unsupported surfaces, reused/older observations, and target mismatches fail closed without actions or writes.

## Legacy samples and simulated actions

`/v1/assist` requires `sessionId` and `prompt`; optional context must reference the same session, be fresh, and have `sensitivity: public`. `responseModes` only describes requested output; it does not invoke desktop speech or overlays. Retrieval uses two bundled keyword-matched sample passages, not an enterprise search service.

Preview accepts `{tool, parameters}`. `view_logs` accepts optional `application` (default MSGuide Demo); `create_work_item` requires `title` and accepts `description`. Both are simulations requiring confirmation. High-risk tools return HTTP 403 with `STEP_UP_REQUIRED`; unknown/blocked tools return `DENIED`. No step-up flow or external execution exists.

Confirm returns `grantToken`, `expiresIn`, `expiresAt`, and `mock`. Execute accepts only `{grantToken}` and uses the stored preview parameters. Grants cannot be replayed. Job states are `pending`, `running`, `success`, `failed`, or `cancelled`; results explicitly indicate simulation. Job records expire after ten minutes; at most 16 mock jobs may be active.

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
| 502 / 504 | Invalid/failed provider result or guidance timeout. |

Capture again and explicitly approve after a desktop error; there is no automatic resend. See [validation](VALIDATION.md) for tests and [model setup](MODEL_SETUP.md) for opt-in remote processing.
