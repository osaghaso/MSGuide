using System.Globalization;
using System.IO;
using System.Text.Json;
using MSGuide.Desktop;

internal static class CaptureProbeProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? hwndValue = null, output = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--hwnd" && hwndValue is null && ++i < args.Length) hwndValue = args[i];
            else if (args[i] == "--output" && output is null && ++i < args.Length) output = args[i];
            else return Fail(output, "invalid-arguments");
        }
        if (hwndValue is null || output is null || !TryHandle(hwndValue, out var hwnd))
            return Fail(output, "invalid-arguments");
        try
        {
            CaptureProbe.WriteJson(hwnd, output);
            return 0;
        }
        catch (OperationCanceledException) { return Fail(output, "cancelled"); }
        catch (InvalidOperationException) { return Fail(output, "window-unavailable-or-changed"); }
        catch (Exception) { return Fail(output, "probe-error"); }
    }

    private static bool TryHandle(string value, out nint hwnd)
    {
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? value[2..] : value;
        bool parsed = long.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long handle);
        hwnd = new nint(handle);
        return parsed && handle != 0;
    }

    private static int Fail(string? output, string code)
    {
        if (output is not null)
        {
            try
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new
                {
                    version = 1,
                    capturedAt = DateTimeOffset.UtcNow,
                    completed = false,
                    failure = code
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })
                    + Environment.NewLine);
            }
            catch { }
        }
        return 1;
    }
}
