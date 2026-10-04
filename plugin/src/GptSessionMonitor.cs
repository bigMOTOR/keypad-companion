using System.Text;
using System.Text.Json;
namespace Loupedeck.MotorControlsPlugin;

// Reads lifecycle markers, never stores messages, prompts, tool arguments, or output.
// Unlike the vendor's foreground-window scan, this also works while another app is open.
internal static class GptSessionMonitor
{
    private sealed class Session
    {
        public string Path, Id, State = "idle";
        public long Position;
        public bool Root;
        public DateTime LastWrite;
    }
    private static readonly Dictionary<string, Session> sessions = new();
    private static DateTime nextDiscovery;
    private static readonly object gate = new();
    public static AgentView Read()
    {
        lock (gate) try
        {
            if (DateTime.UtcNow >= nextDiscovery)
            {
                var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
                if (!Directory.Exists(directory)) return null;
                foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Select(p => new FileInfo(p)).Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)).OrderByDescending(f => f.LastWriteTimeUtc).Take(12))
                    if (!sessions.ContainsKey(file.FullName)) sessions[file.FullName] = new() { Path = file.FullName };
                nextDiscovery = DateTime.UtcNow.AddSeconds(15);
            }
            foreach (var session in sessions.Values)
            {
                var info = new FileInfo(session.Path);
                if (!info.Exists || info.LastWriteTimeUtc == session.LastWrite) continue;
                session.LastWrite = info.LastWriteTimeUtc;
                using var stream = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (session.Position > stream.Length) session.Position = 0;
                stream.Seek(session.Position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (session.Position == 0 || Relevant(line))
                    {
                        JsonDocument doc;
                        try { doc = JsonDocument.Parse(line); } catch { break; }
                        using (doc)
                        {
                            var root = doc.RootElement;
                            var kind = root.GetProperty("type").GetString();
                            var p = root.GetProperty("payload");
                            if (kind == "session_meta")
                            {
                                session.Id = p.GetProperty("id").GetString();
                                session.Root = !p.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("subagent", out _);
                            }
                            else if (kind == "event_msg" && p.TryGetProperty("type", out var type))
                            {
                                switch (type.GetString())
                                {
                                    case "task_started": case "turn_started": session.State = "work"; break;
                                    case "task_complete": case "turn_complete": case "turn_aborted": session.State = "idle"; break;
                                }
                            }
                        }
                    }
                    session.Position += Encoding.UTF8.GetByteCount(line) + 1;
                }
            }
            var selected = sessions.Values.Where(s => s.Root && s.Id != null).OrderBy(s => s.State == "work" && s.LastWrite > DateTime.UtcNow.AddMinutes(-10) ? 0 : 1).ThenByDescending(s => s.LastWrite).FirstOrDefault();
            return selected == null ? null : new(selected.State == "work" && selected.LastWrite > DateTime.UtcNow.AddMinutes(-10) ? "work" : "idle", SessionId: selected.Id);
        }
        catch { return null; }
    }
    private static bool Relevant(string line) => line.Contains("\"task_started\"") || line.Contains("\"task_complete\"") || line.Contains("\"turn_started\"") || line.Contains("\"turn_complete\"") || line.Contains("\"turn_aborted\"");
}
