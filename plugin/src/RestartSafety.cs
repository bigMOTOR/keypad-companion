using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Loupedeck.MotorControlsPlugin;

internal record RestartSafetyResult(bool Safe, string Reason);
internal static class RestartSafetyResponder
{
    private static FileSystemWatcher watcher;
    private static int busy,stopped;
    private static string DirectoryPath => Dashboard.StateDirectory;
    internal static bool ValidRequest(string nonce,DateTimeOffset requested,DateTimeOffset now) => nonce!=null&&Regex.IsMatch(nonce,"^[a-f0-9]{32}$")&&now-requested>=TimeSpan.FromSeconds(-1)&&now-requested<=TimeSpan.FromSeconds(15);
    internal static void Start()
    {
        Interlocked.Exchange(ref stopped,0);Directory.CreateDirectory(DirectoryPath);
        watcher=new(DirectoryPath,"restart-safety-request.json"){NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite};
        watcher.Changed+=(_,_)=>Request();watcher.Created+=(_,_)=>Request();watcher.Renamed+=(_,_)=>Request();watcher.EnableRaisingEvents=true;
    }
    private static void Request()
    {
        if(Volatile.Read(ref stopped)!=0||Interlocked.CompareExchange(ref busy,1,0)!=0)return;
        _=Task.Run(()=>{
            try{
                var request=Path.Combine(DirectoryPath,"restart-safety-request.json");
                if(new FileInfo(request).Length>4096)return;
                using var doc=JsonDocument.Parse(File.ReadAllText(request));var d=doc.RootElement;
                var nonce=d.GetProperty("nonce").GetString();var requested=d.GetProperty("requestedAt").GetDateTimeOffset();
                if(!ValidRequest(nonce,requested,DateTimeOffset.UtcNow))return;
                RestartSafetyResult result;
                try{result=MacDashboard.RestartSafety();}catch{result=new(false,"unavailable");}
                if(Volatile.Read(ref stopped)!=0)return;
                var file=Path.Combine(DirectoryPath,"restart-safety-response.json");var tmp=file+".tmp";
                File.WriteAllText(tmp,JsonSerializer.Serialize(new{nonce,hostPid=Environment.ProcessId,checkedAt=DateTimeOffset.UtcNow,safe=result.Safe,reason=result.Reason}));
                if(OperatingSystem.IsMacOS())File.SetUnixFileMode(tmp,UnixFileMode.UserRead|UnixFileMode.UserWrite);File.Move(tmp,file,true);
            }catch{}finally{Interlocked.Exchange(ref busy,0);}
        });
    }
    internal static void Stop(){Interlocked.Exchange(ref stopped,1);watcher?.Dispose();watcher=null;}
}

internal static partial class MacDashboard
{
    private const string Audio="/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    [StructLayout(LayoutKind.Sequential)] private struct AudioAddress { internal uint Selector,Scope,Element; }
    [DllImport(Audio)] private static extern int AudioObjectGetPropertyDataSize(uint obj,ref AudioAddress address,uint qualifier,IntPtr data,out uint size);
    [DllImport(Audio)] private static extern int AudioObjectGetPropertyData(uint obj,ref AudioAddress address,uint qualifier,IntPtr data,ref uint size,[Out] uint[] values);
    private static uint Code(string text)=>((uint)text[0]<<24)|((uint)text[1]<<16)|((uint)text[2]<<8)|text[3];
    // Includes output devices: muted calls may still have an active incoming stream.
    // Music can therefore defer a restart too. No microphone permission or audio data is requested.
    private static bool? AudioRunning()
    {
        var devices=new AudioAddress{Selector=Code("dev#"),Scope=Code("glob")};
        if(AudioObjectGetPropertyDataSize(1,ref devices,0,IntPtr.Zero,out var size)!=0||size==0||size>4096||size%4!=0)return null;
        var ids=new uint[size/4];if(AudioObjectGetPropertyData(1,ref devices,0,IntPtr.Zero,ref size,ids)!=0)return null;
        foreach(var id in ids){
            var running=new AudioAddress{Selector=Code("gone"),Scope=Code("glob")};uint count=4;var value=new uint[1];
            if(AudioObjectGetPropertyData(id,ref running,0,IntPtr.Zero,ref count,value)!=0||count!=4)return null;
            if(value[0]!=0)return true;
        }
        return false;
    }
    internal static RestartSafetyResult SafetyDecision(bool trusted,bool recording,bool call,bool? audio,bool complete) => !trusted?new(false,"unavailable"):recording?new(false,"recording"):call?new(false,"call"):audio==true?new(false,"audio"):audio==null||!complete?new(false,"unavailable"):new(true,"safe");
    internal static RestartSafetyResult RestartSafety()
    {
        using var s=new Scope();
        if(AXIsProcessTrusted()==0)return new(false,"unavailable");
        if(StopRecording(s)!=IntPtr.Zero)return new(false,"recording");
        var audio=AudioRunning();if(audio!=false)return SafetyDecision(true,false,false,audio,true);
        var generation=MacStateEvents.SafetyGeneration;var timer=Stopwatch.StartNew();
        foreach(var bundle in new[]{"com.apple.screencaptureui","com.apple.screenshot.launcher","com.apple.QuickTimePlayerX","com.obsproject.obs-studio","com.timpler.screenstudio"})
            if(Running(s,bundle).Any())return new(false,"recording");
        // Unsupported call clients cannot prove an idle state: leave them alone.
        foreach(var bundle in new[]{"com.apple.FaceTime","com.microsoft.teams2","com.microsoft.teams","com.hnc.Discord","net.whatsapp.WhatsApp","org.whispersystems.signal-desktop","com.google.Chrome"})
            if(Running(s,bundle).Any())return new(false,"unavailable");
        foreach(var bundle in new[]{"com.apple.Safari","org.mozilla.firefox","com.tinyspeck.slackmacgap","us.zoom.xos"})
        foreach(var app in Running(s,bundle)){
            var root=s.Root(app);var raw=s.Attr(root,"AXWindows");
            if(raw==IntPtr.Zero)return new(false,"unavailable");
            var windows=s.Array(raw).ToArray();if(windows.Length>8)return new(false,"unavailable");
            foreach(var window in windows){
                if(timer.Elapsed.TotalSeconds>=6)return new(false,"unavailable");
                if(bundle=="us.zoom.xos"&&s.Txt(window,"AXIdentifier")=="zm.meeting.window.main")return new(false,"call");
                if(Browsers.Contains(bundle)&&MeetOrigin(LoadedUrl(s,window)))return new(false,"call");
                var result=InspectSafetyWindow(s,window,Browsers.Contains(bundle),timer);
                if(!result.Safe)return result;
            }
        }
        if(timer.Elapsed.TotalSeconds>=6||generation!=MacStateEvents.SafetyGeneration)return new(false,"unavailable");
        if(StopRecording(s)!=IntPtr.Zero)return new(false,"recording");
        return SafetyDecision(true,false,false,AudioRunning(),true);
    }
    private static IEnumerable<App> Running(Scope s,string bundle) => s.Array(SendP(Class("NSRunningApplication"),Sel("runningApplicationsWithBundleIdentifier:"),s.Str(bundle))).Select(p=>new App((int)Get(p,"processIdentifier"),bundle));
    private static RestartSafetyResult InspectSafetyWindow(Scope s,IntPtr window,bool browser,Stopwatch total)
    {
        var queue=new Queue<(IntPtr,bool)>();queue.Enqueue((window,false));var seen=0;var tabs=0;var tabGroup=false;
        while(queue.Count>0){
            if(++seen>1600||total.Elapsed.TotalSeconds>=6)return new(false,"unavailable");
            var(node,inTabs)=queue.Dequeue();var role=s.Txt(node,"AXRole");if(role=="AXTabGroup"){tabGroup=true;inTabs=true;}if(role.Length==0)return new(false,"unavailable");
            if(browser&&role=="AXWebArea")continue;
            if(inTabs&&role is "AXRadioButton" or "AXTab"){tabs++;var label=s.Label(node);if(label.Trim().Length==0)return new(false,"unavailable");if(new[]{"meet","zoom","huddle","дзвінок","розмова"}.Any(label.Contains))return new(false,"call");}
            if(role is "AXButton" or "AXMenuItem" or "AXMenuButton"){
                var label=s.Label(node);
                if(Regex.IsMatch(label,@"^(leave (?:huddle|call|meeting)|end (?:call|meeting)|hang up|покинути (?:зустріч|розмову)|вийти з (?:зустрічі|розмови))\b"))return new(false,"call");
            }
            var children=s.Attr(node,"AXChildren");
            if(children==IntPtr.Zero&&role is "AXWindow" or "AXGroup" or "AXScrollArea" or "AXTabGroup" or "AXWebArea")return new(false,"unavailable");
            foreach(var child in s.Array(children))queue.Enqueue((child,inTabs));
        }
        return browser&&(!tabGroup||tabs==0)?new(false,"unavailable"):new(true,"safe");
    }
}
