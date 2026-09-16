# Teams camera recovery

MSGuide now presents Teams camera recovery as the primary, guide-only journey. The shared editable prompt accepts typed text and local click-to-toggle dictation; camera-help intent is recognized locally without calling the guidance backend.

## Journey

1. Choose the exact Teams window.
2. Inspect Teams controls only.
3. Review the diagnosis.
4. Open `ms-settings:privacy-webcam` through the dedicated user-initiated button.
5. Verify that Windows actually opened the Camera privacy page, then inspect its controls.
6. Show a verified target, if supplied. MSGuide never clicks it.
7. Make the change yourself and check again.
8. Return to Teams.
9. Run the local private verifier.

Permission observed on is an intermediate state. Only a supplied local verifier result with `LocalVerifierPassed == true` can produce the camera-ready state.

Managed/disabled, permission-already-on or wrong-cause, stale/moved, unsupported, unresolved-after-permission, and cancelled outcomes are terminal and explicit. Start over after correcting the condition.

## Privacy

The camera journey does not call the existing full-window `CaptureService` and does not fall back to that raw screenshot workflow. Teams and Settings observations enter through `ICameraRecoverySensing`; the provider must disclose whether it is controls-only, private visual, fixture, or unsupported. The default implementation reports unsupported and captures nothing.

`FixtureCameraRecoverySensing` provides a deterministic test-only journey using the pinned packaged-Teams toggle ID `MSTeams_8wekyb3d8bbwe_ToggleSwitch`. It is never selected by default and ends in the distinct `FixtureComplete` state, not `Ready`. `PendingCameraRecoverySensing` is the explicit runtime fallback and reports unsupported without trying `PrintWindow`.

The Advanced / developer area retains the raw snapshot workflow. Its **Capture / review** action creates a local full-window screenshot before approval controls appear; nothing is uploaded until the user reviews and approves it.

## Integration seam

Connect a sensing implementation before `MainWindow` loads by calling `UseCameraRecoverySensing`. The implementation supplies:

- a Teams control observation and diagnosis;
- a Camera Settings observation and optional verified target;
- target presentation without clicking;
- a final local Teams verification result.

Observations carry the selected Teams window ID so stale or mismatched results fail closed. The target presenter owns any richer evidence or controls-only geometry integration; camera recovery does not add fields to shared contracts.

### Live probe constraints

- New Teams can expose a top-level HWND owned by one process while useful WebView UIA descendants report another process ID. A Teams sensor must stay rooted to the selected HWND but must not discard descendants only because `AutomationElement.Current.ProcessId` differs from the selected top-level PID.
- A live Teams probe exposed useful controls including `more-options-header`, the Settings > Devices tab, `AudioSettings`, `VideoSettings`, the Camera combo box and selected camera text, `open_camera_settings`, and video-setting toggle states. These are probe evidence, not permanent identifiers; sensing must still fail closed when they move or disappear.
- `ms-settings:privacy-webcam` can land on Settings Home when an existing Settings process is alive. Launch success is not page success. A supported observation must report `Page == CameraPrivacy`; otherwise the session enters `WrongSettingsPage` without accepting a toggle or permission claim. MSGuide never closes Settings automatically.
- When the Camera page was reached correctly, rooted UIA exposed cross-process provider elements including `SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch`, `SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch`, `MSTeams_8wekyb3d8bbwe_ToggleSwitch`, and `SystemSettings_CapabilityAccess_Camera_ClassicGlobal_ToggleSwitch`. The pinned golden path is the individual packaged Teams toggle, changed by the user from off to on.
- On the pinned machine, `PrintWindow` flag 0 rendered black while flag 2 rendered Teams and Settings, including the live Teams preview. Flag 2 is undocumented and remains a measured, fail-closed option only. Any image-backed preview may contain personal pixels and must not be persisted.
- Current-machine integration also saw a demo activation failure. It is not treated as camera-sensing acceptance.

## Deterministic tests

`CameraRecoveryTests` covers the happy path and false-ready, wrong-cause, managed, stale, unsupported, unresolved, cancellation, invalid-transition, and local-intent cases. Run it with the existing desktop self-test:

```powershell
$env:MSGUIDE_CAMERA_RECOVERY_TESTS = "1"
desktop\bin\Debug\net10.0-windows\MSGuide.Desktop.exe --self-test --test-results camera-self-test.json
```
