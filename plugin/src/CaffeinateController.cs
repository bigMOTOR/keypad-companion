using System.Diagnostics;
using System.Text.Json;

namespace Loupedeck.MotorControlsPlugin;

public sealed class CaffeinateController : IDisposable
{
    private readonly object gate = new();
    private Process ownedProcess;
    private DateTimeOffset deadline;
    private string error;
    private int durationSeconds;
    public static CaffeinateController Instance { get; } = new();
    private static string StateFile => Path.Combine(AgentBridge.StateDirectory, "caffeinate-until.txt");
    private static string SessionFile => Path.Combine(AgentBridge.StateDirectory, "caffeinate-session.json");
    public CaffeinateController()
    {
        try
        {
            if (File.Exists(StateFile) && DateTimeOffset.TryParse(File.ReadAllText(StateFile), out var until) && until > DateTimeOffset.UtcNow && until <= DateTimeOffset.UtcNow.AddHours(24))
            {
                var total = 7200; // Preserve legacy two-hour sessions across this update.
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(SessionFile));
                    if (doc.RootElement.GetProperty("until").GetDateTimeOffset() == until && doc.RootElement.GetProperty("durationSeconds").GetInt32() is var saved && saved is >= 1 and <= 86400) total = saved;
                }
                catch { }
                Start((int)Math.Ceiling((until-DateTimeOffset.UtcNow).TotalSeconds), total, until);
            }
        }
        catch { }
    }

    public (bool On, int Minutes, string Error, double Fraction) State
    {
        get
        {
            lock (gate)
            {
                var on = ownedProcess != null && !ownedProcess.HasExited;
                return (on, on ? Math.Max(1, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalMinutes)) : 0, error, on && durationSeconds > 0 ? Math.Clamp((deadline-DateTimeOffset.UtcNow).TotalSeconds / durationSeconds, 0, 1) : 0);
            }
        }
    }

    public void Toggle(int? seconds = null)
    {
        lock (gate)
        {
            error = null;
            try
            {
                if (ownedProcess != null && !ownedProcess.HasExited)
                {
                    Stop();
                    try { File.Delete(StateFile); } catch { }
                    try { File.Delete(SessionFile); } catch { }
                    return;
                }
                var requested = seconds ?? CaffeinateSettings.ReadMinutes() * 60;
                if (requested is < 1 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
                Start(requested, requested);
            }
            catch (Exception ex) { error = ex.Message; }
        }
    }

    private void Start(int seconds, int total, DateTimeOffset? restoreDeadline = null)
    {
                ownedProcess?.Dispose();
                var start = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
                start.ArgumentList.Add("-di");
                start.ArgumentList.Add("-t");
                start.ArgumentList.Add(seconds.ToString());
                start.ArgumentList.Add("-w");
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                ownedProcess = Process.Start(start);
                deadline = restoreDeadline ?? DateTimeOffset.UtcNow.AddSeconds(seconds);
                durationSeconds = total;
                // An invalid command can exit immediately. Never report that as ON.
                if (ownedProcess.WaitForExit(100))
                    error = "Could not keep Mac awake";
                else
                {
                    Directory.CreateDirectory(AgentBridge.StateDirectory);
                    File.WriteAllText(StateFile, deadline.ToString("O"));
                    File.WriteAllText(SessionFile, JsonSerializer.Serialize(new { until = deadline, durationSeconds }));
                    if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(StateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(SessionFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
    }

    private void Stop()
    {
        // Only terminate the exact Process object this plugin created.
        if (ownedProcess != null && !ownedProcess.HasExited)
        {
            ownedProcess.Kill();
            ownedProcess.WaitForExit(2000);
        }
        ownedProcess?.Dispose();
        ownedProcess = null;
    }

    public void Dispose() { lock (gate) { Stop(); } }
}
