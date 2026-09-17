# Teams camera recovery

MSGuide presents a question-first companion with a contextual Teams camera workflow. **Guide me**
keeps every action with the user. **Fix it for me** can invoke one freshly
verified Teams camera control after explicit approval; closing and relaunching
Teams requires a second approval.
Device-wide **Camera access** and user-wide **Let apps access your camera**
changes each require their own, clearly scoped approval. Permission approval
never authorizes the subsequent Teams camera-on action.

## Companion shell

Type a question and select **Ask MSGuide**, or press **Enter** in the question
box. **Shift+Enter** inserts a newline. Typing edits a draft and revokes prior
approval; it does not start a recovery. Asking starts read-only diagnosis of
the selected Teams window, never an approved camera or permission change.
Unsupported questions receive an explicit scope explanation rather than silently
starting a camera workflow.

The product header describes help with Microsoft tools, not a specific camera
scenario. The question starts blank and the interaction mode is available
beside the composer. The camera card appears only for a camera request.

The desktop shell keeps one contextual camera action visible at a time. Ask,
choose the exact Teams window, and follow the primary button in the
camera card. Progress, the current step, and the selected window remain together
so loading, reinitialization, terminal errors, and verified completion are not
mistaken for success.

Mode choices are locked only while a recovery is active. Fixture completion and
**Stop recovery** unlock them; **Start over** returns to the editable mode state
instead of immediately beginning another repair. If the previous Teams window
is still available, Start over performs a cancellable, read-only reassessment.
A working camera is reported as **Already working**; otherwise the current
problem is described while modes remain editable. No actionable target or
approval carries over from this status check.
During an active check or recovery, **Stop & switch** beside the mode choices
cancels the run and clears its approval before selecting the other mode.
Asking again starts fresh; switching never approves or invokes a camera action.
Choosing a Teams window triggers read-only inspection automatically.

**Settings** contains microphone selection, connection status, and privacy
controls. **Voice settings** opens microphone selection directly. Connection
checks do not cancel an active camera task.

Build Center and Notepad are available only with `-DeveloperTools`; normal
launch does not show developer/demo controls. Requests without an automated
fix offer **Use screen context**, which opens the existing explicit
choose-window/capture/review/approve flow. Merely opening it does not capture,
upload, or grant control authority. Shared screen guidance remains limited to
public or synthetic content and its privacy disclosure stays visible.

The camera card's
privacy chips summarize the active contract: controls-only inspection, a
local visual check, and either no automatic clicks or approved actions only.

## Journey

1. Choose the exact Teams meeting, prejoin, or Devices window.
2. Inspect device-wide, user-app, and Teams camera permission evidence before
   offering a Teams camera action. A device-wide denial takes precedence over
   a stored Teams `Allow` value.
3. If Teams exposes one exact enabled **Turn camera on** button or off Camera
   toggle, MSGuide treats that as the primary target.
4. In **Guide me**, show the target, turn it on yourself, and check again.
5. In **Fix it for me**, approve one invocation of that exact freshly
   reacquired UI Automation control. There is no `SendInput` fallback or
   automatic retry.
6. Verify that Teams now exposes the camera-on state and Windows reports active
   Teams camera use.
7. If active use still cannot be verified, approve a separate Teams restart or
   reopen the camera surface yourself, then verify again.

If any required permission is off, MSGuide explains the first blocking level and
offers **Open Camera settings**. After verifying that page, it presents only the
first enabled, off permission toggle. Disabled child toggles beneath a disabled
parent are not mistaken for policy-managed settings. After each approval,
MSGuide rereads the page; a different blocking toggle needs a new approval.
Once global permissions are on, it reinspects Teams before requesting camera-on
approval. It never changes policy, retries an action, or treats permission-on
as camera readiness.

Permission observed on is an intermediate state. Only a supplied local verifier result with `LocalVerifierPassed == true` can produce the camera-ready state.
An already-on Teams control plus current Windows active-use evidence can establish
readiness in a fresh session without a new off-to-on transition. When MSGuide did
observe a repair in this session, the verifier still requires active-use evidence
from after that repair. A camera-on observation is verified automatically; it is
not itself a readiness claim.

Managed/disabled, permission-already-on or wrong-cause, stale/moved, unsupported, unresolved-after-permission, and cancelled outcomes are terminal and explicit. Start over after correcting the condition.

## Privacy

The initial Teams and Camera Settings inspections use UI Automation only and do
not create screenshot pixels. A visible Teams camera-on state is verified
against Windows camera-use evidence. The permission fallback's **Private visual
check** can additionally capture two selected-window frames locally, compare a
bounded preview region for motion, and dispose the evidence without uploading
or saving it. Camera-control mode uses only `TogglePattern` or `InvokePattern`
on the exact revalidated target. It never falls back to coordinates or synthetic
input. Teams restart is a separate approval because it can end an active meeting.

`FixtureCameraRecoverySensing` provides a deterministic test-only journey with
a simulated Teams camera-on target. **Fix it for me** invokes it once and
completes only after simulated local verification. The permission fallback
fixture remains covered by deterministic tests. Fixture completion ends in the
distinct `FixtureComplete` state, not `Ready`.

`LiveCameraRecoverySensing` is the default. Set
`MSGUIDE_CAMERA_RECOVERY_MODE=fixture` or launch with `-CameraFixture` to run the
clearly labelled deterministic fixture. Use `disabled` only to test the explicit
unsupported fallback.

The contextual **Screen context** view retains the snapshot workflow. Its
**Capture and review** action creates a local full-window screenshot before
approval controls appear; nothing is uploaded until the user reviews and
approves it. Developer mode also exposes this view for diagnostics.

## Integration seam

Connect a sensing implementation before `MainWindow` loads by calling `UseCameraRecoverySensing`. The implementation supplies:

- a Teams control observation and diagnosis;
- a Camera Settings observation and optional verified target;
- target presentation without clicking;
- a final local Teams verification result.

An implementation may also expose `ICameraRecoveryControl`. That optional
interface performs one approved target action and a separately approved Teams
restart. Sensing remains usable without control support.

Observations carry the selected Teams window ID so stale or mismatched results fail closed. The target presenter owns any richer evidence or controls-only geometry integration; camera recovery does not add fields to shared contracts.

The live provider requires the user to reopen Teams Devices or prejoin after
permission restoration. If Teams recreates its HWND during relaunch, selecting
the reopened Teams window rebinds the verification step without discarding the
already observed permission transition.

### Live probe constraints

- The camera-control branch is currently pinned to English UI Automation names
  exposed as **Turn camera on**, **Turn camera off**, or an off/on **Camera**
  toggle. Teams shortcut suffixes such as **Turn camera on (Ctrl+Shift+O)** are
  normalized before matching. Other locales fail closed until their accessible
  names are explicitly supported.
- New Teams can expose a top-level HWND owned by one process while useful WebView UIA descendants report another process ID. A Teams sensor must stay rooted to the selected HWND but must not discard descendants only because `AutomationElement.Current.ProcessId` differs from the selected top-level PID.
- The meeting prejoin camera toolbar can appear at raw UIA depth 25. Teams
  sensing uses a bounded depth of 32; Windows Settings and generic snapshots
  keep depth 18. Existing node, text, element, and time budgets remain bounded.
  An incomplete camera read cannot authorize an action, claim readiness, or
  fall through to a permission diagnosis.
- A 20-run census found Teams `VideoSettings` and the Camera Settings packaged-Teams toggle 20/20, with each rooted UIA read completing in 119–232 ms despite provider PIDs differing from top-level PIDs. These are pinned-machine measurements, not general guarantees.
- `open_camera_settings` was not realized in those 20 reads. The journey must use the hard-coded `ms-settings:privacy-webcam` launch and verify the resulting page instead of relying on that Teams button.
- `ms-settings:privacy-webcam` can land on Settings Home when an existing Settings process is alive. Launch success is not page success. A supported observation must report `Page == CameraPrivacy`; otherwise the session enters `WrongSettingsPage` without accepting a toggle or permission claim. MSGuide never closes Settings automatically.
- When the Camera page was reached correctly, rooted UIA exposed cross-process provider elements including `SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch`, `SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch`, `MSTeams_8wekyb3d8bbwe_ToggleSwitch`, and `SystemSettings_CapabilityAccess_Camera_ClassicGlobal_ToggleSwitch`. All three relevant visible states were on throughout the census. An initial on observation ends as already-on/wrong-cause; it does not force the permission story. The pinned golden fixture still models the individual packaged Teams toggle changed by the user from off to on.
- On the pinned machine, `PrintWindow` flag 0 rendered black while flag 2 rendered Teams and Settings, including the live Teams preview. Flag 2 is undocumented and remains a measured, fail-closed option only. Any image-backed preview may contain personal pixels and must not be persisted.
- A reversible live test showed that changing the packaged Teams toggle while a preview was already active caused no immediate Camera combo-box, error, or preview-contrast change in either direction after four seconds. A live toggle change is therefore neither failure nor recovery evidence. The deterministic demo must prepare permission Off before Teams initializes its camera, and final readiness may require a user-driven prejoin/camera-surface reopen or Teams relaunch.
- Current-machine integration also saw a demo activation failure. It is not treated as camera-sensing acceptance.

## Deterministic tests

`CameraRecoveryTests` covers the happy path and false-ready, wrong-cause, managed, stale, unsupported, unresolved, cancellation, invalid-transition, and local-intent cases. Run it with the existing desktop self-test:

```powershell
$env:MSGUIDE_CAMERA_RECOVERY_TESTS = "1"
desktop\bin\Debug\net10.0-windows10.0.19041.0\MSGuide.Desktop.exe --self-test --test-results camera-self-test.json
```

## Test the journey

### Fixture first

```powershell
.\scripts\Start-MSGuide.ps1 -CameraFixture -Shareable
```

`-Shareable` is an explicit demo-only opt-in. MSGuide remains excluded from
screen capture by default; in Teams, share the full **Screen** rather than the
Teams application window.

1. Open Teams and choose **Fix it for me**.
2. Start recovery and choose a Teams window.
3. Select **Inspect controls only**.
4. Select **Approve & turn camera on**.
5. The fixture ends as **Fixture complete**, never real camera-ready.

### Pinned live machine

1. Open a Teams meeting or prejoin screen with its camera button off.
2. Start MSGuide normally and choose **Fix it for me**.
3. Select that exact Teams window and inspect controls.
4. Review the verified target and approve **Turn camera on**.
5. If Windows does not report active Teams camera use, approve the separate
   Teams restart or reopen the camera surface manually.

For the permission fallback, leave both global Camera toggles on and turn only
the packaged **Microsoft Teams** toggle off before Teams initializes its camera.

To exercise the device-wide branch, turn **Camera access** off. MSGuide should
diagnose that parent block even when the stored Teams permission is `Allow`,
then offer a separate device-wide approval in the verified Camera privacy page.

The live check reaches **Verified** only when the Teams Camera selector is
enabled, Windows reports the packaged Teams app using the camera, and two local
preview-region frames show motion.
