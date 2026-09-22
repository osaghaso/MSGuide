using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private CameraRecoverySession? advancingCameraSession;

    private async Task ContinueCameraRepairAsync()
    {
        var session = cameraRecovery;
        if (closing || ReferenceEquals(advancingCameraSession, session)) return;
        advancingCameraSession = session;
        try
        {
            for (int step = 0; step < 8; step++)
            {
                if (!ReferenceEquals(cameraRecovery, session) || cameraRecoveryBusy
                    || session.IsTerminal || closing || cameraRecoveryNotice is not null) return;
                var previous = session.State;
                if (session.CanOpenSettings && session.Mode == CameraRecoveryInteractionMode.Control)
                    await OpenCameraSettingsAsync();
                else if (session.CanInspectSettings)
                    await ObserveCameraSettings();
                else if (session.CanReturnToTeams)
                    ReturnToSelectedTeams("Camera permission is on. Continuing with fresh Teams inspection.");
                else if (session.CanInspectTeams)
                    await ObserveSelectedTeamsAsync();
                else if (session.State == CameraRecoveryState.NeedsLocalVerification)
                    await VerifySelectedTeamsAsync();
                else if (session.ConsumeCameraOnRequest())
                    await ActivateCameraTargetAsync();
                else return; // Permission/restart approval, manual selection, or an unsupported step.
                if (session.State == previous) return;
            }
            if (ReferenceEquals(cameraRecovery, session) && !session.IsTerminal)
            {
                session.MarkUnsupported("Camera recovery did not reach a stable next step. No further action was attempted.");
                UpdateCameraRecoveryUi();
            }
        }
        finally
        {
            if (ReferenceEquals(advancingCameraSession, session)) advancingCameraSession = null;
        }
    }

    private async Task OpenCameraSettingsAsync()
    {
        if (!cameraRecovery.CanOpenSettings || cameraRecoveryBusy) return;
        cameraRecoveryNotice = null;
        cameraRecovery.PrepareToOpenSettings();
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Opening Camera privacy settings for read-only inspection. Any permission change will ask first.";
        UpdateCameraRecoveryUi();
        try
        {
            if (cameraRecoverySensing.Mode != CameraRecoverySensingMode.Fixture)
            {
                using var launched = Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam")
                { UseShellExecute = true });
            }
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecovery.MarkSettingsOpened();
            cameraRecoveryNotice = null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            cameraRecoveryNotice = null;
            cameraRecovery.MarkUnsupported(
                "Windows could not open Camera privacy settings. No alternate command or setting change was attempted.");
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi();
        }
        if (cameraRecovery.CanInspectSettings && CurrentCameraOperation(generation, token))
            await ObserveCameraSettings(waitForPage: true);
    }
}
