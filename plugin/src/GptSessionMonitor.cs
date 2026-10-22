namespace Loupedeck.MotorControlsPlugin;

// Reads lifecycle markers, never stores messages, prompts, tool arguments, or output.
// Unlike the vendor's foreground-window scan, this also works while another app is open.
internal static class GptSessionMonitor
{
    private sealed class Session
    {
        public string Path;
        public SessionLogCursor Cursor = new();
        public string Id => Cursor.Id;
        public string State => Cursor.State;
        public bool Root => Cursor.Root;
        public DateTime LastWrite;
    }
    private static readonly Dictionary<string, Session> sessions = new();
    private static DateTime nextDiscovery, nextRead;
    private static int dirty=1, discoveryDirty=1;
    internal static void Invalidate(bool discovery=false){Interlocked.Exchange(ref dirty,1);if(discovery)Interlocked.Exchange(ref discoveryDirty,1);}
    private static readonly object gate = new();
    public static AgentView Read()
    {
        lock (gate) try
        {
            var changed=Interlocked.Exchange(ref dirty,0)!=0;
            if (DateTime.UtcNow >= nextDiscovery || Interlocked.Exchange(ref discoveryDirty,0)!=0)
            {
                var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
                if (!Directory.Exists(directory)) return null;
                var recent = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Select(p => new FileInfo(p)).Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)).OrderByDescending(f => f.LastWriteTimeUtc).Take(12).ToArray();
                var paths = recent.Select(f => f.FullName).ToHashSet();
                foreach (var path in sessions.Keys.Where(p => !paths.Contains(p)).ToArray()) sessions.Remove(path);
                foreach (var file in recent)
                    if (!sessions.ContainsKey(file.FullName)) sessions[file.FullName] = new() { Path = file.FullName };
                nextDiscovery = DateTime.UtcNow.AddMinutes(1);
            }
            if(changed || DateTime.UtcNow>=nextRead) { nextRead=DateTime.UtcNow.AddMinutes(1);
            foreach (var session in sessions.Values)
            {
                var info = new FileInfo(session.Path);
                if (!info.Exists || info.LastWriteTimeUtc == session.LastWrite) continue;
                using var stream = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                session.Cursor.Read(stream);
                session.LastWrite = info.LastWriteTimeUtc;
            }
            }
            var selected = sessions.Values.Where(s => s.Root && s.Id != null).OrderBy(s => s.State == "work" && s.LastWrite > DateTime.UtcNow.AddMinutes(-10) ? 0 : 1).ThenByDescending(s => s.LastWrite).FirstOrDefault();
            return selected == null ? null : new(selected.State == "work" && selected.LastWrite > DateTime.UtcNow.AddMinutes(-10) ? "work" : "idle", SessionId: selected.Id);
        }
        catch { return null; }
    }
}
