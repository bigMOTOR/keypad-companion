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
    // Calls and audio deliberately do not block recovery. Only recording is inspected.
    internal static RestartSafetyResult SafetyDecision(bool trusted,bool recording) => !trusted?new(false,"unavailable"):recording?new(false,"recording"):new(true,"safe");
    internal static RestartSafetyResult RestartSafety()
    {
        using var s=new Scope();
        if(AXIsProcessTrusted()==0)return SafetyDecision(false,false);
        var generation=MacStateEvents.SafetyGeneration;
        if(StopRecording(s)!=IntPtr.Zero)return SafetyDecision(true,true);
        foreach(var bundle in new[]{"com.apple.screencaptureui","com.apple.screenshot.launcher","com.apple.QuickTimePlayerX","com.obsproject.obs-studio","com.timpler.screenstudio"})
            if(Running(s,bundle).Any())return SafetyDecision(true,true);
        var recording=StopRecording(s)!=IntPtr.Zero;
        if(generation!=MacStateEvents.SafetyGeneration)return new(false,"unavailable");
        return SafetyDecision(true,recording);
    }
    private static IEnumerable<App> Running(Scope s,string bundle) => s.Array(SendP(Class("NSRunningApplication"),Sel("runningApplicationsWithBundleIdentifier:"),s.Str(bundle))).Select(p=>new App((int)Get(p,"processIdentifier"),bundle));
}
