# Teams camera recovery

MSGuide now presents Teams camera recovery as the primary, guide-only journey. The shared editable prompt accepts typed text and local click-to-toggle dictation; camera-help intent is recognized locally without calling the guidance backend.

## Journey

1. Choose the exact Teams window.
2. Inspect Teams controls only.
3. Review the diagnosis.
4. Open `ms-settings:privacy-webcam` through the dedicated user-initiated button.
5. Inspect Camera Settings controls only.
6. Show a verified target, if supplied. MSGuide never clicks it.
7. Make the change yourself and check again.
8. Return to Teams.
9. Run the local private verifier.

Permission observed on is an intermediate state. Only a supplied local verifier result with `LocalVerifierPassed == true` can produce the camera-ready state.

Managed/disabled, permission-already-on or wrong-cause, stale/moved, unsupported, unresolved-after-permission, and cancelled outcomes are terminal and explicit. Start over after correcting the condition.

## Privacy

The camera journey does not call the existing full-window `CaptureService`. It requests controls-only sensing through `ICameraRecoverySensing` and does not fall back to a screenshot. The default implementation reports unsupported because controls-only sensing is not available on this branch.

`FixtureCameraRecoverySensing` provides a deterministic test-only journey. It is never selected by default and ends in the distinct `FixtureComplete` state, not `Ready`. `PendingCameraRecoverySensing` is the explicit runtime fallback and reports unsupported without trying `PrintWindow`.

The Advanced / developer area retains the raw snapshot workflow. Its **Capture / review** action creates a local full-window screenshot before approval controls appear; nothing is uploaded until the user reviews and approves it.

## Integration seam

Connect a sensing implementation before `MainWindow` loads by calling `UseCameraRecoverySensing`. The implementation supplies:

- a Teams control observation and diagnosis;
- a Camera Settings observation and optional verified target;
- target presentation without clicking;
- a final local Teams verification result.

Observations carry the selected Teams window ID so stale or mismatched results fail closed. The target presenter owns any richer evidence or controls-only geometry integration; camera recovery does not add fields to shared contracts.

## Deterministic tests

`CameraRecoveryTests` covers the happy path and false-ready, wrong-cause, managed, stale, unsupported, unresolved, cancellation, invalid-transition, and local-intent cases. Run it with the existing desktop self-test:

```powershell
$env:MSGUIDE_CAMERA_RECOVERY_TESTS = "1"
desktop\bin\Debug\net10.0-windows\MSGuide.Desktop.exe --self-test --test-results camera-self-test.json
```
