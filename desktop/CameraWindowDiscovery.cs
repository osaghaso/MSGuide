namespace MSGuide.Desktop;

internal enum CameraSurfaceFinding { MeetingCamera, NoCamera, Incomplete }

internal sealed record CameraWindowSelection(WindowChoice? Window, string Detail);

internal interface ICameraWindowDiscovery
{
    Task<CameraSurfaceFinding> InspectCameraSurfaceAsync(WindowChoice window, CancellationToken cancellationToken);
}

internal static class CameraWindowDiscovery
{
    private const int MaxCandidates = 6;

    internal static async Task<CameraWindowSelection> SelectAsync(
        IReadOnlyList<WindowChoice> candidates, WindowChoice? invoked,
        ICameraWindowDiscovery discovery, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var windows = candidates.Where(window => window.IsMicrosoftTeamsWindow)
            .DistinctBy(window => window.Id).ToArray();
        if (windows.Length == 0)
            return new(null, "Open a Teams meeting or prejoin with its camera controls visible, then check again.");
        if (windows.Length > MaxCandidates)
            return new(null, "Too many Teams windows are open to inspect safely. Close unused windows or choose the meeting explicitly.");

        var matches = new List<WindowChoice>();
        bool incomplete = false;
        foreach (var window in windows.OrderByDescending(window => window.Id == invoked?.Id))
        {
            ct.ThrowIfCancellationRequested();
            var result = await discovery.InspectCameraSurfaceAsync(window, ct);
            ct.ThrowIfCancellationRequested();
            if (result == CameraSurfaceFinding.MeetingCamera)
            {
                if (window.Id == invoked?.Id)
                    return new(window, "Using the Teams meeting or prejoin you invoked MSGuide from.");
                matches.Add(window);
            }
            else if (result == CameraSurfaceFinding.Incomplete) incomplete = true;
        }
        if (incomplete)
            return new(null, "A Teams window could not be inspected completely. Choose the intended meeting; no camera action was prepared.");
        return matches.Count switch
        {
            1 => new(matches[0], "Found the Teams meeting or prejoin with a visible camera control."),
            > 1 => new(null, "More than one Teams meeting has camera controls. Choose which meeting to fix."),
            _ => new(null, "No visible meeting or prejoin camera control was found. Open that Teams surface, then check again.")
        };
    }

    internal static CameraSurfaceFinding Assess(IEnumerable<ElementInfo> elements)
    {
        int count = elements.Count(CameraRecoveryPinnedTargets.IsTeamsCameraElement);
        return count switch
        {
            0 => CameraSurfaceFinding.NoCamera,
            1 => CameraSurfaceFinding.MeetingCamera,
            _ => CameraSurfaceFinding.Incomplete
        };
    }
}
