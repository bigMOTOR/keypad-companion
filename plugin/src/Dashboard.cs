using System.Runtime.InteropServices;
using System.Text.Json;
namespace Loupedeck.MotorControlsPlugin;

internal record DashboardSnapshot(string Context = "general", bool Recording = false, int Seconds = 0,
    bool? Mic = null, bool? Camera = null, bool? Sharing = null, bool Trusted = false, bool Preview = false);
internal record DashboardTile(string Action, string Image);
// A brief AX tree redraw must not erase unrelated, freshly observed controls.
// Never reuse call state across a different window/meeting or beyond two seconds.
internal sealed class CallStateMemory
{
    private string key="";
    private readonly bool?[] states=new bool?[3];
    private readonly DateTime[] seen=new DateTime[3];
    internal void Clear(){key="";Array.Clear(states);Array.Clear(seen);}
    internal (bool? Mic,bool? Camera,bool? Share) Merge(string nextKey,DateTime now,bool? mic,bool? camera,bool? share)
    {
        if(string.IsNullOrEmpty(nextKey)){Clear();return(null,null,null);}
        if(nextKey!=key){Clear();key=nextKey;}
        var incoming=new[]{mic,camera,share};
        for(var i=0;i<3;i++)if(incoming[i].HasValue){states[i]=incoming[i];seen[i]=now;}else if(now-seen[i]>TimeSpan.FromSeconds(2))states[i]=null;
        return(states[0],states[1],states[2]);
    }
}
internal static class DashboardLayout
{
    internal static DashboardTile[] Tiles(DashboardSnapshot s)
    {
        var capture = new DashboardTile("capture", s.Recording ? "capture-stop" : "capture");
        var last = new DashboardTile("last", "last");
        var context = new DashboardTile("context", "context");
        var blank = new DashboardTile(null, "blank");
        DashboardTile Call(string action, bool? on) => new(action, action + (on.HasValue ? on.Value ? "-on" : "-off" : "-unknown"));
        return s.Context switch
        {
            "prusa" => new[] { capture, new("prusa-default", "prusa-default"), new("prusa-preview", s.Preview ? "prusa-editor" : "prusa-preview"), new("prusa-left", "prusa-left"), new("prusa-top", "prusa-top"), new("prusa-right", "prusa-right") },
            "xcode" => new[] { capture, new("error", "error"), blank, blank, blank, blank },
            "browser" => new[] { capture, last, context, blank, new("new-meet", "new-meet"), blank },
            "slack" or "meet" or "zoom" => new[] { capture, blank, blank, Call("mic", s.Mic), Call("camera", s.Camera), Call("share", s.Sharing) },
            _ => new[] { capture, last, context, blank, blank, blank }
        };
    }
}
internal static class Dashboard
{
    private static readonly object gate = new();
    private static readonly CancellationTokenSource stopping=new();
    internal static void Stop()=>stopping.Cancel();
    private static int invalidated=1;
    internal static void Invalidate(){Interlocked.Exchange(ref invalidated,1);TileSignals.Raise(TileGroup.Dashboard);}
    internal static int NextTileRefreshMilliseconds=>current.Recording||DateTime.UtcNow<noticeUntil?1000:60000;
    private static DateTime nextRefresh;
    private static int refreshing, acting;
    private static DashboardSnapshot current = new();
    private static DateTime sampledAt;
    private static long nativeReads;
    internal static Func<DashboardSnapshot> SnapshotReader=MacDashboard.Snapshot;
    internal static string StateDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","KeypadBrightness");
    private static string notice;
    private static int noticeSlot;
    private static DateTime noticeUntil;
    private static bool reportedError;
    internal static DashboardSnapshot Read()
    {
        if (!stopping.IsCancellationRequested && (Volatile.Read(ref invalidated)!=0 || DateTime.UtcNow >= nextRefresh) && Interlocked.CompareExchange(ref refreshing, 1, 0) == 0)
            _ = Task.Run(() => { Interlocked.Exchange(ref invalidated,0); try { lock(gate) { using var doc=Native(false); Interlocked.Increment(ref nativeReads);current=Parse(doc.RootElement);sampledAt=DateTime.UtcNow; Publish(); } } catch(Exception e) { if(!reportedError) { reportedError=true; PluginLog.Error("Dashboard: "+e.GetType().Name+" "+e.Message); } } finally { nextRefresh=DateTime.UtcNow.AddMinutes(1); Interlocked.Exchange(ref refreshing,0);TileSignals.Raise(TileGroup.Dashboard); } });
        return current.Recording ? current with { Seconds=current.Seconds+(int)Math.Max(0,(DateTime.UtcNow-sampledAt).TotalSeconds) } : current;
    }
    internal static DashboardTile Tile(int slot) => DashboardLayout.Tiles(Read())[slot];
    internal static string Notice(int slot) => slot == noticeSlot && DateTime.UtcNow < noticeUntil ? notice : null;
    internal static bool Busy => Volatile.Read(ref acting) != 0;
    internal static void Run(int slot)
    {
        if (stopping.IsCancellationRequested || Interlocked.CompareExchange(ref acting,1,0) != 0) return;
        _ = Task.Run(() => {
            try {
                lock(gate) {
                    // Resolve against fresh foreground state, never against a previously drawn tile.
                    using var state=Native(false); current=Parse(state.RootElement);
                    var tile=DashboardLayout.Tiles(current)[slot];
                    if (tile.Action == null) return;
                    using var result=Native(true,tile.Action);
                    notice=result.RootElement.TryGetProperty("message",out var message)?message.GetString():"";
                    PluginLog.Info("Dashboard "+tile.Action+": "+notice);
                    if(tile.Action=="capture")MacStateEvents.TrackCapture();
                    if(tile.Action=="new-meet"){try{var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","KeypadBrightness","meet-result.json");File.WriteAllText(file,JsonSerializer.Serialize(new{message=notice,updatedAt=DateTime.UtcNow}));}catch{}}
                    noticeSlot=slot; noticeUntil=DateTime.UtcNow.AddSeconds(6); nextRefresh=DateTime.MinValue; Publish();
                }
            } catch { noticeSlot=slot; notice="Дія недоступна"; noticeUntil=DateTime.UtcNow.AddSeconds(6); }
            finally { Interlocked.Exchange(ref acting,0);Invalidate(); }
        });
    }
    private static string lastPublished;
    private static void Publish()
    {
        var body=JsonSerializer.Serialize(new { scheduling=new { nativeReads=Interlocked.Read(ref nativeReads),recoverySeconds=60,events=MacStateEvents.Diagnostics }, context=current.Context,recording=current.Recording,seconds=current.Seconds,mic=current.Mic,camera=current.Camera,sharing=current.Sharing,trusted=current.Trusted,preview=current.Preview,notice=DateTime.UtcNow<noticeUntil?notice:null,tiles=DashboardLayout.Tiles(current).Select(t=>t.Image) });
        if(body==lastPublished)return;
        var directory=StateDirectory;
        Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"dashboard-state.json");
        File.WriteAllText(file+".tmp",body);File.Move(file+".tmp",file,true);lastPublished=body;
    }
    private static JsonDocument Native(bool execute, string action = null)
    {
        return execute ? JsonSerializer.SerializeToDocument(new { message=MacDashboard.Execute(action,stopping.Token) }) : JsonSerializer.SerializeToDocument(SnapshotReader(), new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase });
    }

    internal static DashboardSnapshot Parse(JsonElement e)
    {
        bool? Optional(string name) => e.TryGetProperty(name,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():null;
        var name=e.TryGetProperty("context",out var c)?c.GetString():"general";
        if (name is not ("general" or "prusa" or "xcode" or "browser" or "slack" or "meet" or "zoom")) name="general";
        return new(name,Optional("recording")==true,e.TryGetProperty("seconds",out var t)&&t.TryGetInt32(out var seconds)?Math.Max(0,seconds):0,Optional("mic"),Optional("camera"),Optional("sharing"),Optional("trusted")==true,Optional("preview")==true);
    }
}
