using System.IO;
using System.Text.Json;

namespace MSGuide.Desktop;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly string? Path = Environment.GetEnvironmentVariable("MSGUIDE_DESKTOP_LOG");
    private const long MaxBytes = 1_000_000;

    internal static void Record(string eventName, object fields)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        try
        {
            lock (Gate)
            {
                string? directory = System.IO.Path.GetDirectoryName(Path);
                if (string.IsNullOrWhiteSpace(directory)) return;
                Directory.CreateDirectory(directory);
                if (File.Exists(Path) && new FileInfo(Path).Length >= MaxBytes)
                    File.Move(Path, Path + ".1", true);
                string json = JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    eventName,
                    fields,
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                File.AppendAllText(Path, json + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            // Diagnostics must never interrupt the requested operation.
        }
    }
}
