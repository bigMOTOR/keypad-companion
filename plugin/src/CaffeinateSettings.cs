using System.Text.Json;
namespace Loupedeck.MotorControlsPlugin;
internal static class CaffeinateSettings
{
    public static int ReadMinutes(string path = null)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "KeypadBrightness", "caffeine-settings.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("durationMinutes", out var value) && value.TryGetInt32(out var minutes) && minutes is >= 1 and <= 1440) return minutes;
        }
        catch { }
        return 120;
    }
}
