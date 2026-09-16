# Teams camera recovery

MSGuide now presents Teams camera recovery as the primary, guide-only journey. The shared editable prompt accepts typed text and local click-to-toggle dictation; camera-help intent is recognized locally without calling the guidance backend.

## Companion shell

The desktop shell keeps one contextual camera action visible at a time. Start
recovery, choose the exact Teams window, and follow the primary button in the
camera card. Progress, the current step, and the selected window remain together
so loading, reinitialization, terminal errors, and verified completion are not
mistaken for success.

Build Center, Notepad, service checks, raw snapshot diagnostics, and detailed
privacy/technical notes are collapsed under **Advanced**. The camera card's
privacy chips summarize the active contract: controls-only inspection, a
user-initiated local visual check, and no automatic clicks.

## Journey

1. Choose the exact Teams window.
2. Inspect Teams controls only.
3. Review the diagnosis.
4. Open `ms-settings:privacy-webcam` through the dedicated user-initiated button.
5. Verify that Windows actually opened the Camera privacy page, then inspect its controls.
6. Show a verified target, if supplied. MSGuide never clicks it.
7. Make the change yourself and check again.
8. Return to Teams.
9. Run the local private verifier. If the existing camera session survived the permission change, reopen prejoin or the camera surface, or relaunch Teams yourself, then verify again.

Permission observed on is an intermediate state. Only a supplied local verifier result with `LocalVerifierPassed == true` can produce the camera-ready state.

Managed/disabled, permission-already-on or wrong-cause, stale/moved, unsupported, unresolved-after-permission, and cancelled outcomes are terminal and explicit. Start over after correcting the condition.

## Privacy

The initial Teams and Camera Settings inspections use UI Automation only and do
not create screenshot pixels. The final **Private visual check** captures two
selected-window frames locally through Windows Graphics Capture, compares a
bounded preview region for motion, and disposes the evidence without uploading
or saving it. This check is initiated only by the user's button click.

`FixtureCameraRecoverySensing` provides a deterministic test-only journey using the pinned packaged-Teams toggle ID `MSTeams_8wekyb3d8bbwe_ToggleSwitch`. Prepare it with permission Off before Teams initializes the camera. The fixture requires a simulated camera reinitialization check and ends in the distinct `FixtureComplete` state, not `Ready`. `PendingCameraRecoverySensing` is the explicit runtime fallback and reports unsupported without trying `PrintWindow`.

`LiveCameraRecoverySensing` is the default. Set
`MSGUIDE_CAMERA_RECOVERY_MODE=fixture` or launch with `-CameraFixture` to run the
clearly labelled deterministic fixture. Use `disabled` only to test the explicit
unsupported fallback.

The Advanced / developer area retains the raw snapshot workflow. Its **Capture / review** action creates a local full-window screenshot before approval controls appear; nothing is uploaded until the user reviews and approves it.

## Integration seam

Connect a sensing implementation before `MainWindow` loads by calling `UseCameraRecoverySensing`. The implementation supplies:

- a Teams control observation and diagnosis;
- a Camera Settings observation and optional verified target;
- target presentation without clicking;
- a final local Teams verification result.

Observations carry the selected Teams window ID so stale or mismatched results fail closed. The target presenter owns any richer evidence or controls-only geometry integration; camera recovery does not add fields to shared contracts.

The live provider requires the user to reopen Teams Devices or prejoin after
permission restoration. If Teams recreates its HWND during relaunch, selecting
the reopened Teams window rebinds the verification step without discarding the
already observed permission transition.

### Live probe constraints

- New Teams can expose a top-level HWND owned by one process while useful WebView UIA descendants report another process ID. A Teams sensor must stay rooted to the selected HWND but must not discard descendants only because `AutomationElement.Current.ProcessId` differs from the selected top-level PID.
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
.\scripts\Start-MSGuide.ps1 -CameraFixture
```

1. Open Teams and choose its window in the camera card.
2. Run the complete guide. The first verification requests reinitialization;
   the second completes as **Fixture complete**, never real camera-ready.

### Pinned live machine

1. Close Teams.
2. Open `ms-settings:privacy-webcam`.
3. Leave both global Camera toggles on and turn only the packaged
   **Microsoft Teams** toggle off.
4. Start Teams, open **Settings > Devices**, and keep the Video section and
   Preview visible.
5. Start MSGuide normally and follow the camera recovery card.
6. After MSGuide outlines the exact Teams permission, turn it on yourself.
7. Reopen Teams Devices or relaunch Teams, reselect the new Teams window if
   needed, and move slightly in the preview during **Private visual check**.

The live check reaches **Verified** only when the Teams Camera selector is
enabled, Windows reports the packaged Teams app using the camera, and two local
preview-region frames show motion.
