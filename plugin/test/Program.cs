using System.Text.Json;
using Loupedeck.MotorControlsPlugin;
var images=args.Length>0?args[0]:Path.Combine("plugin","src","Images");
void Check(bool value,string message){if(!value)throw new Exception(message);}
foreach(var context in new[]{"general","browser","prusa","xcode","slack","meet","zoom"})foreach(var recording in new[]{false,true}){
 var tiles=DashboardLayout.Tiles(new(Context:context,Recording:recording));
 Check(tiles.Length==6,"Dashboard grid changed");Check(tiles[0].Action=="capture","Capture moved for "+context);Check(tiles[0].Image==(recording?"capture-stop":"capture"),"Recording state was lost across app change");
 foreach(var t in tiles)Check(File.Exists(Path.Combine(images,"dashboard-"+t.Image+".png")),"Missing tile image "+t.Image);
}
Check(string.Join(",",DashboardLayout.Tiles(new(Context:"browser")).Select(t=>t.Action))=="capture,last,context,,new-meet,","Browser layout differs from the accepted layout");
Check(string.Join(",",DashboardLayout.Tiles(new(Context:"prusa")).Select(t=>t.Action))=="capture,prusa-default,prusa-preview,prusa-left,prusa-top,prusa-right","Prusa layout differs from accepted preview");
foreach(var context in new[]{"slack","meet","zoom"}){
 var unknown=DashboardLayout.Tiles(new(Context:context));Check(unknown[3].Image=="mic-unknown"&&unknown[4].Image=="camera-unknown"&&unknown[5].Image=="share-unknown","Unknown call state shown as on/off");
 var known=DashboardLayout.Tiles(new(Context:context,Mic:false,Camera:true,Sharing:true));Check(known[3].Image=="mic-off"&&known[4].Image=="camera-on"&&known[5].Image=="share-on","Real call state mismatch");
}
using(var d=JsonDocument.Parse("{\"context\":\"meet\",\"recording\":true,\"seconds\":-9,\"mic\":false}")){
 var s=Dashboard.Parse(d.RootElement);Check(s.Seconds==0&&s.Camera==null&&s.Mic==false&&s.Recording,"Native response validation failed");
}
using(var d=JsonDocument.Parse("{\"context\":\"unknown-app\"}"))Check(Dashboard.Parse(d.RootElement).Context=="general","Unknown app fallback broken");
Console.WriteLine("Dashboard: capture fixed across 14 states; layout fidelity; missing call data; real call states; native response validation passed.");

foreach(var url in new[]{"http://meet.google.com/abc-defg-hij","https://meet.google.com.evil.com/abc-defg-hij","https://meet.google.com/","https://meet.google.com/abc-defg-hij/extra","https://meet.google.com:8443/abc-defg-hij","https://someone@meet.google.com/abc-defg-hij"})Check(MacDashboard.MeetingUrl(url)==null,"Unvalidated Meet URL accepted");
Check(MacDashboard.MeetingUrl("https://meet.google.com/abc-defg-hij?authuser=1")=="https://meet.google.com/abc-defg-hij","Meet copy includes unrelated URL data");

foreach(var url in new[]{"https//meet.google.com/","https://www.https.com//meet.google.com/","https://meet.google.com.evil.com/","https://evil@meet.google.com/","https://meet.google.com:8443/"," https://meet.google.com/","https://meet.google.com/\n","https://meet.google.com\t.evil.com/"})Check(!MacDashboard.MeetOrigin(url),"Unsafe origin accepted: "+url);
Check(MacDashboard.MeetOrigin(MacDashboard.MeetHome),"Exact Meet home rejected");
Console.WriteLine("Meet: malformed URL, advertising-domain reproduction, deceptive hosts, credentials, port and whitespace guards passed.");

foreach(var (action,label,on) in new (string,string,bool)[]{
 ("mic","Turn off microphone",true),("mic","Turn on microphone",false),
 ("mic","Mute audio",true),("mic","Unmute audio",false),
 ("mic","Audio, turn off, currently unmuted Mute",true),("mic","Audio, turn on, currently muted Unmute",false),
 ("camera","Turn off camera",true),("camera","Turn on camera",false),
 ("camera","Stop video",true),("camera","Start video",false),
 ("camera","Video, turn off, currently on Stop video",true),("camera","Video, turn on, currently off Start video",false),
 ("share","Share screen",false),("share","Stop presenting",true),
 ("share","You are presenting",true),("share","Start share",false),("share","Stop share",true)
})Check(MacDashboard.CallLabelState(action,label)==on,"Call status misread: "+label);
foreach(var label in new[]{"Audio settings","Video settings","Microphone options","Mute media capture","Mute all","Ask all to unmute","Camera settings"})foreach(var action in new[]{"mic","camera","share"})Check(MacDashboard.CallLabelState(action,label)==null,"Non-toggle selected: "+label);
Check(!MacDashboard.ExplicitShareStop("You are presenting"),"Sharing indicator incorrectly treated as a Stop action");
Check(MacDashboard.ExplicitShareStop("Stop presenting")&&MacDashboard.ExplicitShareStop("Зупинити показ"),"Explicit Stop action rejected");
var memory=new CallStateMemory();var now=DateTime.UtcNow;
memory.Merge("meeting-a",now,false,false,false);
var changed=memory.Merge("meeting-a",now.AddSeconds(1),null,true,true);
Check(changed.Mic==false&&changed.Camera==true&&changed.Share==true,"Changing camera/share erased muted microphone");
Check(memory.Merge("meeting-a",now.AddSeconds(3),null,null,null).Mic==null,"Expired state retained");
Check(memory.Merge("meeting-b",now.AddSeconds(3),null,null,null)==(null,null,null),"Call state leaked into a different meeting");
Console.WriteLine("Calls: actual Meet/Zoom labels, non-toggle exclusion, independent redraw recovery and cross-meeting isolation passed.");

Dashboard.StateDirectory=Path.Combine(Path.GetTempPath(),"keypad-scheduling-test-"+Guid.NewGuid());
var reads=0;
Dashboard.SnapshotReader=()=>{Interlocked.Increment(ref reads);return new(Context:"general");};
for(var i=0;i<100;i++)Dashboard.Read();
for(var i=0;i<100&&Volatile.Read(ref reads)==0;i++)Thread.Sleep(10);
Thread.Sleep(100);Check(reads==1,"Concurrent tiles caused repeated native snapshots");
for(var i=0;i<100;i++)Dashboard.Read();Thread.Sleep(100);Check(reads==1,"Idle reads bypassed one-minute cache");
Dashboard.SnapshotReader=()=>{Interlocked.Increment(ref reads);return new(Context:"meet",Mic:false,Camera:true);};
Dashboard.Invalidate();Dashboard.Read();
for(var i=0;i<100&&Dashboard.Read().Context!="meet";i++)Thread.Sleep(10);
Check(reads==2&&Dashboard.Read().Mic==false,"Event invalidation failed to update meeting state");
Thread.Sleep(2100);Dashboard.Read();Thread.Sleep(100);Check(reads==2,"Calls still polled every two seconds");
Console.WriteLine("Scheduling: one shared snapshot for concurrent tiles; idle and call reads cached; event invalidation refreshes immediately.");
foreach(var (seconds,total,min,max) in new (double,int,int,int)[]{(7200,7200,35000,37000),(7190,7200,25000,27000),(7140,7200,47000,49000),(0,7200,1000,1000)}){
 var delay=RefreshPolicy.CoffeeDelay(seconds,total);Check(delay>=min&&delay<=max,"Coffee display boundary scheduling failed");
}
Console.WriteLine("Coffee: minute and progress boundaries avoid unnecessary per-second checks.");
Dashboard.Stop();Directory.Delete(Dashboard.StateDirectory,true);

namespace Loupedeck.MotorControlsPlugin { internal static class PluginLog { internal static void Error(string message){} internal static void Info(string message){} } }

namespace Loupedeck.MotorControlsPlugin { internal class MotorControlsPlugin { internal static MotorControlsPlugin Current => null; internal Loupedeck.KeyboardApi KeyboardApi => null; } }

namespace Loupedeck.MotorControlsPlugin { [Flags] internal enum TileGroup { Dashboard=1 } internal static class TileSignals { internal static void Raise(TileGroup group){} } }
