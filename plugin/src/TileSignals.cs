namespace Loupedeck.MotorControlsPlugin;
[Flags]
internal enum TileGroup { Dashboard=1, Gpt=2, Claude=4, Coffee=8, All=15 }
internal static class TileSignals
{
    internal static event Action<TileGroup> Changed;
    internal static void Raise(TileGroup group) { foreach(var handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>()) try { ((Action<TileGroup>)handler)(group); } catch { } }
}
// File notifications carry only paths. No conversation text or credentials are cached.
internal static class TileFileEvents
{
    private static readonly List<FileSystemWatcher> watchers=new();
    internal static void Start()
    {
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Watch(Path.Combine(home,".codex","sessions"),"*.jsonl",true,TileGroup.Gpt,true);
        Watch(Path.Combine(home,"Library","Application Support","KeypadBrightness"),"claude-quota.json",false,TileGroup.Claude);
        Watch(AgentBridge.StateDirectory,"claude-quota*.json",false,TileGroup.Claude);
        Watch(Path.Combine(home,".claude"),"settings.json",false,TileGroup.Claude);
    }
    private static void Watch(string path,string filter,bool recursive,TileGroup group,bool sessions=false)
    {
        if(!Directory.Exists(path))return;
        var w=new FileSystemWatcher(path,filter){IncludeSubdirectories=recursive,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size};
        void Change(bool discovery){if(sessions)GptSessionMonitor.Invalidate(discovery);if(group==TileGroup.Gpt)AgentBridge.InvalidateGpt();if(group==TileGroup.Claude)AgentBridge.InvalidateClaude();TileSignals.Raise(group);}
        w.Changed+=(_,_)=>Change(false);w.Created+=(_,_)=>Change(true);w.Deleted+=(_,_)=>Change(true);w.Renamed+=(_,_)=>Change(true);w.Error+=(_,_)=>Change(true);
        watchers.Add(w);w.EnableRaisingEvents=true;
    }
    internal static void Stop(){foreach(var w in watchers)w.Dispose();watchers.Clear();}
}
