using System.Collections;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
namespace Loupedeck.MotorControlsPlugin;

internal record AgentView(string State, int? Remaining = null, string SessionId = null);
internal static class AgentBridge
{
    private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static object manager;
    private static Type monitor;
    private static bool started;
    private static int? remaining;
    private static long reset;
    private static DateTimeOffset updated, nextRefresh;
    private static int refreshing;
    private static readonly object gate = new();
    public static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MotorAI");
    private static object Manager
    {
        get
        {
            if (manager != null) return manager;
            if (MotorControlsPlugin.Current == null) return null;
            foreach (var member in typeof(Plugin).GetFields(Inst))
                if (member.GetValue(MotorControlsPlugin.Current) is Delegate callbacks)
                    foreach (var callback in callbacks.GetInvocationList())
                        if (callback.Target?.GetType().FullName == "Loupedeck.Service.PluginManager") return manager = callback.Target;
            return null;
        }
    }
    private static Plugin Loaded(string name) => Manager?.GetType().GetMethod("GetPlugin", new[] { typeof(string) })?.Invoke(Manager, new object[] { name }) as Plugin;
    private static object Prop(object obj, string name) => obj?.GetType().GetProperty(name, Inst)?.GetValue(obj);
    private static object Service(Plugin plugin, string type) => plugin?.GetType().GetFields(Inst).FirstOrDefault(f => f.FieldType.Name == type)?.GetValue(plugin);
    private static AgentView gptCached = new("unknown");
    private static int readingGpt,gptGeneration;
    private static DateTime readGptAfter;
    internal static void InvalidateGpt(){Interlocked.Increment(ref gptGeneration);readGptAfter=DateTime.MinValue;}
    public static AgentView Gpt()
    {
        if(DateTime.UtcNow>=readGptAfter&&Interlocked.CompareExchange(ref readingGpt,1,0)==0)
            _=Task.Run(()=>{var generation=Volatile.Read(ref gptGeneration);try{gptCached=ReadGpt();}finally{readGptAfter=generation==Volatile.Read(ref gptGeneration)?DateTime.UtcNow.AddMinutes(1):DateTime.MinValue;TileSignals.Raise(TileGroup.Gpt);Interlocked.Exchange(ref readingGpt,0);}});
        return gptCached;
    }
    private static AgentView ReadGpt()
    {
        try
        {
            var plugin = Loaded("CodexDesktop"); if (plugin == null) return new("unknown");
            monitor ??= plugin.Assembly.GetType("Loupedeck.CodexDesktopPlugin.CodexMacStateMonitor");
            if (!started && monitor != null)
            { monitor.GetMethod("StartStop", Stat)?.Invoke(null, null); monitor.GetMethod("StartApproval", Stat)?.Invoke(null, null); started = true; }
            if (DateTimeOffset.UtcNow >= nextRefresh && Interlocked.CompareExchange(ref refreshing, 1, 0) == 0) _ = RefreshQuota(plugin.Assembly);
            var session = GptSessionMonitor.Read();
            if (session?.State == "work") { lock(gate) return session with { Remaining = reset > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? remaining : null }; }
            if (session == null && monitor?.GetProperty("HasActiveTurn", Stat)?.GetValue(null) is true) return new("work", reset > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? remaining : null);
            lock (gate) return remaining.HasValue && reset > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                ? new(DateTimeOffset.UtcNow - updated > TimeSpan.FromMinutes(10) ? "stale" : "idle", remaining, session?.SessionId) : new("unknown", SessionId: session?.SessionId);
        }
        catch { return new("unknown"); }
    }
    private static async Task RefreshQuota(Assembly assembly)
    {
        try
        {
            object[] args = { null };
            if (assembly.GetType("Loupedeck.CodexDesktopPlugin.CodexLocator")?.GetMethod("TryFindCodexExecutable", Stat)?.Invoke(null, args) is not true || args[0] is not string executable) return;
            using var p = new Process { StartInfo = new(executable, "app-server") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            p.Start(); _ = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await p.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"motor-keypad\",\"version\":\"1.1.0\"}}}"); await p.StandardInput.FlushAsync();
                using var initialized = await Response(p, 1, timeout.Token);
                await p.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
                await p.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}"); await p.StandardInput.FlushAsync();
                using var reply = await Response(p, 2, timeout.Token); var quota = ParseWeeklyQuota(reply.RootElement);
                if (quota.Remaining.HasValue) lock (gate) { remaining = quota.Remaining; reset = quota.Reset; updated = DateTimeOffset.UtcNow; }
            }
            finally { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        }
        catch { /* Never show fabricated data. A cached value becomes visibly stale. */ }
        finally { nextRefresh = DateTimeOffset.UtcNow.AddMinutes(2); Interlocked.Exchange(ref refreshing, 0);InvalidateGpt();TileSignals.Raise(TileGroup.Gpt); }
    }
    private static async Task<JsonDocument> Response(Process p, int id, CancellationToken token)
    {
        while (true) { var line = await p.StandardOutput.ReadLineAsync(token); if (line == null) throw new EndOfStreamException(); var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var value) && value.TryGetInt32(out var actual) && actual == id) return doc; doc.Dispose(); }
    }
    internal static (int? Remaining, long Reset) ParseWeeklyQuota(JsonElement reply)
    {
        if (!reply.TryGetProperty("result", out var result)) return (null, 0);
        JsonElement limits;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out limits)) { }
        else if (!result.TryGetProperty("rateLimits", out limits)) return (null, 0);
        foreach (var key in new[] { "primary", "secondary" })
            if (limits.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("windowDurationMins", out var duration) && duration.GetInt32() == 10080 && w.TryGetProperty("usedPercent", out var used))
                return ((int)Math.Round(Math.Clamp(100 - used.GetDouble(), 0, 100), MidpointRounding.AwayFromZero), w.TryGetProperty("resetsAt", out var end) ? end.GetInt64() : 0);
        return (null, 0);
    }
    private static DateTime claudeQuotaAfter;
    private static int? claudeRemaining;
    private static long claudeReset;
    private static bool claudeStale;
    private static DateTimeOffset claudeUpdated;
    internal static void InvalidateClaude()=>claudeQuotaAfter=DateTime.MinValue;
    public static AgentView Claude()
    {
        try
        {
            var registry = Service(Loaded("ClaudeDesktop"), "SessionRegistryService");
            var all = (registry?.GetType().GetMethod("GetSessions")?.Invoke(registry, null) as IEnumerable)?.Cast<object>();
            var selected = all?.Where(s => Prop(s, "HasEnded") is not true && Prop(s, "EffectiveStatus")?.ToString() != "Closed")
                .OrderBy(s => Priority(Prop(s, "EffectiveStatus")?.ToString())).ThenByDescending(s => Prop(s, "LastInteractionAt") is DateTimeOffset t ? t : DateTimeOffset.MinValue).FirstOrDefault();
            var id = Prop(selected, "SessionId") as string;
            var state = Prop(selected, "EffectiveStatus")?.ToString();

            if(DateTime.UtcNow>=claudeQuotaAfter) {
                claudeQuotaAfter=DateTime.UtcNow.AddMinutes(1);claudeRemaining=null;
                var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","KeypadBrightness","claude-quota.json");
                if(!File.Exists(file))file=Path.Combine(StateDirectory,"claude-quota.json");
                if(File.Exists(file)){using var doc=JsonDocument.Parse(File.ReadAllText(file));var d=doc.RootElement;
                    claudeReset=d.GetProperty("resetsAt").GetInt64();claudeUpdated=d.GetProperty("updatedAt").GetDateTimeOffset();claudeRemaining=d.GetProperty("remaining").GetInt32();claudeStale=d.TryGetProperty("stale",out var stale)&&stale.ValueKind==JsonValueKind.True;}
            }
            if(claudeRemaining is >=0 and <=100&&claudeReset>DateTimeOffset.UtcNow.ToUnixTimeSeconds()&&DateTimeOffset.UtcNow-claudeUpdated>=TimeSpan.FromMinutes(-1)&&DateTimeOffset.UtcNow-claudeUpdated<=TimeSpan.FromMinutes(30))return new(state=="Working"?"work":claudeStale||DateTimeOffset.UtcNow-claudeUpdated>=TimeSpan.FromMinutes(10)?"stale":"idle",claudeRemaining,id);
            return new(state == "Working" ? "work" : "unknown", SessionId: id);
        }
        catch { return new("unknown"); }
    }
    private static int Priority(string status) => status switch { "NeedsInput" => 0, "Working" => 1, _ => 2 };
    public static void FocusGpt()
    {
        var view = Gpt(); var plugin = Loaded("CodexDesktop");
        if (view.SessionId != null) { var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add($"codex://threads/{Uri.EscapeDataString(view.SessionId)}"); Process.Start(start)?.Dispose(); return; }
        if (plugin == null) { Open("/Applications/ChatGPT.app"); return; }
        plugin.DynamicCommands.TryRunCommand("Loupedeck.CodexDesktopPlugin.OpenCodexCommand", null);
        if (view.State == "wait") plugin.DynamicCommands.TryRunCommand("Loupedeck.CodexDesktopPlugin.NextChatNeedingAttentionCommand", null);
    }
    public static void FocusClaude()
    {
        var view = Claude(); var links = Service(Loaded("ClaudeDesktop"), "DeepLinkService");
        if (view.SessionId != null && links?.GetType().GetMethod("ResumeSession")?.Invoke(links, new object[] { view.SessionId }) is true) return;
        Open("/Applications/Claude.app");
    }
    private static void Open(string app) { var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add("-a"); start.ArgumentList.Add(app); Process.Start(start)?.Dispose(); }
    public static void Dispose() { if (!started) return; monitor?.GetMethod("StopStop", Stat)?.Invoke(null, null); monitor?.GetMethod("StopApproval", Stat)?.Invoke(null, null); started = false; }
}
