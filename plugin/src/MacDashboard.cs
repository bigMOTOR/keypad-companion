using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Loupedeck.MotorControlsPlugin;

// Apple system frameworks only: runs inside the already-authorized Logitech host.
internal static class MacDashboard
{
    private const string ObjC="/usr/lib/libobjc.A.dylib", CF="/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", AX="/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const ulong Cmd=1UL<<20, Shift=1UL<<17, Ctrl=1UL<<18;
    private static readonly HashSet<string> Browsers=new(){"com.apple.Safari","org.mozilla.firefox","com.google.Chrome"};
    private static readonly string[] Mic={"microphone","мікрофон","микрофон"}, Camera={"camera","video on","video off","turn on video","turn off video","камера","камеру"}, Share={"share screen","sharing","present now","presenting","показ екрана","демонстрац","зупинити показ"};
    private static App previous;
    private static DateTime? recordingBegan;
    private static readonly CallStateMemory callMemory=new();
    private record App(int Pid,string Bundle);
    static MacDashboard() { NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"); }
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern IntPtr Send(IntPtr receiver,IntPtr selector);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern IntPtr SendP(IntPtr receiver,IntPtr selector,IntPtr arg);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern IntPtr SendPP(IntPtr receiver,IntPtr selector,IntPtr arg1,IntPtr arg2);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern IntPtr SendN(IntPtr receiver,IntPtr selector,nint arg);
    [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(CF)] private static extern void CFRelease(IntPtr value);
    [DllImport(CF)] private static extern byte CFEqual(IntPtr a,IntPtr b);
    [DllImport(CF)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(CF)] private static extern nuint CFStringGetTypeID();
    [DllImport(CF)] private static extern nuint CFURLGetTypeID();
    [DllImport(CF)] private static extern IntPtr CFURLGetString(IntPtr value);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,[MarshalAs(UnmanagedType.LPUTF8Str)] string text,uint encoding);
    [DllImport(CF)] private static extern nint CFStringGetLength(IntPtr text);
    [DllImport(CF)] private static extern nint CFStringGetMaximumSizeForEncoding(nint length,uint encoding);
    [DllImport(CF)] private static extern byte CFStringGetCString(IntPtr text,byte[] buffer,nint length,uint encoding);
    [DllImport(CF)] private static extern nuint CFArrayGetTypeID();
    [DllImport(CF)] private static extern nuint CFBooleanGetTypeID();
    [DllImport(CF)] private static extern byte CFBooleanGetValue(IntPtr value);
    [DllImport(CF)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array,nint index);
    [DllImport(AX)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(AX)] private static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(AX)] private static extern nuint AXUIElementGetTypeID();
    [DllImport(AX)] private static extern int AXUIElementGetPid(IntPtr element,out int pid);
    [DllImport(AX)] private static extern int AXUIElementCopyAttributeValue(IntPtr element,IntPtr attribute,out IntPtr value);
    [DllImport(AX)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element,float seconds);
    [DllImport(AX)] private static extern int AXUIElementSetAttributeValue(IntPtr element,IntPtr attribute,IntPtr value);
    [DllImport(AX)] private static extern int AXUIElementPerformAction(IntPtr element,IntPtr action);
    [DllImport(AX)] private static extern int AXUIElementCopyActionNames(IntPtr element,out IntPtr names);
    [DllImport(AX)] private static extern byte AXIsProcessTrusted();
    [DllImport(AX)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source,ushort virtualKey,[MarshalAs(UnmanagedType.I1)] bool keyDown);
    [DllImport(AX)] private static extern void CGEventSetFlags(IntPtr @event,ulong flags);
    [DllImport(AX)] private static extern void CGEventPostToPid(int pid,IntPtr @event);
    private static IntPtr Class(string name)=>objc_getClass(name);
    private static IntPtr Sel(string name)=>sel_registerName(name);
    private static IntPtr Get(IntPtr value,string selector)=>value==IntPtr.Zero?IntPtr.Zero:Send(value,Sel(selector));
    private static string Text(IntPtr value)
    {
        if(value==IntPtr.Zero)return "";
        var type=CFGetTypeID(value);
        if(type==CFURLGetTypeID())value=CFURLGetString(value);
        else if(type!=CFStringGetTypeID())return "";
        var length=CFStringGetMaximumSizeForEncoding(CFStringGetLength(value),0x08000100)+1;
        if(length<1||length>131072)return "";
        var bytes=new byte[(int)length];
        if(CFStringGetCString(value,bytes,length,0x08000100)==0)return "";
        var end=Array.IndexOf(bytes,(byte)0);return System.Text.Encoding.UTF8.GetString(bytes,0,end<0?bytes.Length:end);
    }
    // All copied CF/AX values belong to one bounded read/action and are released together.
    private sealed class Scope:IDisposable
    {
        private readonly IntPtr pool=objc_autoreleasePoolPush();
        private readonly List<IntPtr> owned=new();
        private readonly Dictionary<string,IntPtr> strings=new();
        internal IntPtr Hold(IntPtr p){if(p!=IntPtr.Zero)owned.Add(p);return p;}
        internal IntPtr Str(string value){if(!strings.TryGetValue(value,out var p)){p=Hold(CFStringCreateWithCString(IntPtr.Zero,value,0x08000100));strings[value]=p;}return p;}
        internal IntPtr Attr(IntPtr element,string name){if(element==IntPtr.Zero)return IntPtr.Zero;AXUIElementSetMessagingTimeout(element,0.06f);return AXUIElementCopyAttributeValue(element,Str(name),out var p)==0?Hold(p):IntPtr.Zero;}
        internal string Txt(IntPtr element,string name)=>Text(Attr(element,name));
        internal bool IsTrue(IntPtr element,string name){var value=Attr(element,name);return value!=IntPtr.Zero&&CFGetTypeID(value)==CFBooleanGetTypeID()&&CFBooleanGetValue(value)!=0;}
        internal IntPtr Element(IntPtr element,string name){var p=Attr(element,name);return p!=IntPtr.Zero&&CFGetTypeID(p)==AXUIElementGetTypeID()?p:IntPtr.Zero;}
        internal IEnumerable<IntPtr> Array(IntPtr array){if(array==IntPtr.Zero||CFGetTypeID(array)!=CFArrayGetTypeID())yield break;var count=Math.Min(2000,(int)CFArrayGetCount(array));for(var i=0;i<count;i++)yield return CFArrayGetValueAtIndex(array,i);}
        internal IntPtr Root(App app)=>app==null?IntPtr.Zero:Hold(AXUIElementCreateApplication(app.Pid));
        internal IntPtr Window(App app){var root=Root(app);var p=Element(root,"AXFocusedWindow");return p!=IntPtr.Zero?p:Array(Attr(root,"AXWindows")).FirstOrDefault();}
        internal string Label(IntPtr p)=>(Txt(p,"AXTitle")+" "+Txt(p,"AXDescription")+" "+Txt(p,"AXHelp")).ToLowerInvariant();
        internal IntPtr Scan(IntPtr root,Func<IntPtr,bool> match,int limit=450,double seconds=.3,bool chromeOnly=false)
        {
            if(root==IntPtr.Zero)return IntPtr.Zero;
            var timer=Stopwatch.StartNew();var queue=new Queue<(IntPtr,int)>();queue.Enqueue((root,0));var seen=0;
            while(queue.Count>0&&seen++<limit&&timer.Elapsed.TotalSeconds<seconds){var(node,depth)=queue.Dequeue();if(match(node))return node;if(depth<24&&(!chromeOnly||Txt(node,"AXRole")!="AXWebArea"))foreach(var child in Array(Attr(node,"AXChildren")))queue.Enqueue((child,depth+1));}
            return IntPtr.Zero;
        }
        internal bool SetText(IntPtr element,string text)=>element!=IntPtr.Zero&&AXUIElementSetAttributeValue(element,Str("AXValue"),Str(text))==0;
        internal bool HasAction(IntPtr element,string action)=>element!=IntPtr.Zero&&AXUIElementCopyActionNames(element,out var names)==0&&Array(Hold(names)).Any(p=>Text(p)==action);
        internal bool Action(IntPtr element,string action)=>element!=IntPtr.Zero&&AXUIElementPerformAction(element,Str(action))==0;
        internal bool Press(IntPtr element)=>Action(element,"AXPress");
        public void Dispose(){for(var i=owned.Count-1;i>=0;i--)CFRelease(owned[i]);objc_autoreleasePoolPop(pool);}
    }
    private static IntPtr Workspace()=>Get(Class("NSWorkspace"),"sharedWorkspace");
    private static App Foreground()
    {
        using var s=new Scope();var focused=s.Element(s.Hold(AXUIElementCreateSystemWide()),"AXFocusedApplication");
        var app=focused!=IntPtr.Zero&&AXUIElementGetPid(focused,out var pid)==0?SendN(Class("NSRunningApplication"),Sel("runningApplicationWithProcessIdentifier:"),pid):Get(Workspace(),"frontmostApplication");
        return app==IntPtr.Zero?null:new((int)Get(app,"processIdentifier"),Text(Get(app,"bundleIdentifier")));
    }
    private static App Current()
    {
        var app=Foreground();
        if(app!=null&&app.Bundle!="com.logi.optionsplus"&&app.Bundle!="com.apple.screencaptureui"&&app.Bundle!="com.apple.screenshot.launcher"&&app.Bundle!="com.apple.controlcenter"&&!app.Bundle.Contains("LogiPlugin"))previous=app;
        if(previous!=null&&SendN(Class("NSRunningApplication"),Sel("runningApplicationWithProcessIdentifier:"),previous.Pid)!=IntPtr.Zero)return previous;
        return app;
    }
    private static bool Active(App app)=>app!=null&&Foreground()?.Pid==app.Pid;
    internal const string MeetHome="https://meet.google.com/";
    internal static bool MeetOrigin(string value)=>value!=null&&value.StartsWith("https://",StringComparison.Ordinal)&&!value.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c))&&Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Host=="meet.google.com"&&u.IsDefaultPort&&u.UserInfo.Length==0;
    internal static string MeetingUrl(string value)=>MeetOrigin(value)&&Uri.TryCreate(value,UriKind.Absolute,out var u)&&Regex.IsMatch(u.AbsolutePath,@"^/[a-z]{3}-[a-z]{4}-[a-z]{3}$")?"https://meet.google.com"+u.AbsolutePath:null;
    // Loaded document only. Address-field text is never proof of the page's origin.
    private static string LoadedUrl(Scope s,IntPtr window)
    {
        var document=s.Txt(window,"AXDocument");
        if(Uri.TryCreate(document,UriKind.Absolute,out _))return document;
        var area=s.Scan(window,node=>s.Txt(node,"AXRole")=="AXWebArea",300,.25);
        return s.Txt(area,"AXURL");
    }
    private static bool SameWindow(Scope s,App app,IntPtr window){var current=s.Window(app);return Active(app)&&window!=IntPtr.Zero&&current!=IntPtr.Zero&&CFEqual(window,current)!=0;}
    private static IntPtr Focused(Scope s,App app){var focused=s.Element(s.Root(app),"AXFocusedUIElement");return focused!=IntPtr.Zero?focused:s.Element(s.Hold(AXUIElementCreateSystemWide()),"AXFocusedUIElement");}
    private static void MeetStep(string step,object details=null)
    {
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","KeypadBrightness");
        try{Directory.CreateDirectory(dir);var file=Path.Combine(dir,"meet-operation.json");File.WriteAllText(file+".tmp",JsonSerializer.Serialize(new{step,details,updatedAt=DateTime.UtcNow}));File.Move(file+".tmp",file,true);}catch{}
    }
    private static bool IsAddress(Scope s,IntPtr node)
    {
        if(s.Txt(node,"AXRole") is not ("AXTextField" or "AXComboBox"))return false;
        var id=s.Txt(node,"AXIdentifier");var label=s.Label(node);
        return Regex.IsMatch(label,@"search with .+ or enter address")||id=="WEB_BROWSER_ADDRESS_AND_SEARCH_FIELD"||id=="urlbar-input"||new[]{"address and search bar","address and search","smart search field","search or enter address"}.Any(label.Contains);
    }
    private static bool BrowserChrome(Scope s,IntPtr field,IntPtr window)
    {
        if(field==IntPtr.Zero||window==IntPtr.Zero)return false;
        var node=field;
        for(var depth=0;node!=IntPtr.Zero&&depth<32;depth++){
            if(s.Txt(node,"AXRole")=="AXWebArea")return false;
            if(CFEqual(node,window)!=0)return true;
            var parent=s.Element(node,"AXParent");
            if(parent==IntPtr.Zero||CFEqual(parent,node)!=0)return false;
            node=parent;
        }
        return false;
    }
    private static IntPtr Address(Scope s,App app,IntPtr window){var focused=Focused(s,app);return IsAddress(s,focused)&&BrowserChrome(s,focused,window)?focused:s.Scan(window,node=>IsAddress(s,node)&&BrowserChrome(s,node,window),350,.7,true);}
    private static bool AddressReady(Scope s,App app,IntPtr window,IntPtr field)
    {
        var focused=Focused(s,app);
        return SameWindow(s,app,window)&&field!=IntPtr.Zero&&focused!=IntPtr.Zero&&CFEqual(field,focused)!=0&&BrowserChrome(s,field,window)&&s.Txt(field,"AXValue")==MeetHome;
    }
    private static bool ConfirmAddress(Scope s,App app,IntPtr window,IntPtr field,CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested||!AddressReady(s,app,window,field))return false;
        if(s.HasAction(field,"AXConfirm"))return s.Action(field,"AXConfirm");
        // Firefox exposes no AXConfirm. Send one unmodified Return to that process
        // only, after verifying the whole URL and focused native address field.
        // This never enters Logitech's asynchronous character/shortcut queue.
        if(app.Bundle!="org.mozilla.firefox")return false;
        var down=s.Hold(CGEventCreateKeyboardEvent(IntPtr.Zero,36,true));
        var up=s.Hold(CGEventCreateKeyboardEvent(IntPtr.Zero,36,false));
        if(down==IntPtr.Zero||up==IntPtr.Zero)return false;
        CGEventSetFlags(down,0);CGEventSetFlags(up,0);
        if(cancellation.IsCancellationRequested||!AddressReady(s,app,window,field))return false;
        CGEventPostToPid(app.Pid,down);
        CGEventPostToPid(app.Pid,up);
        return true;
    }
    private static int TabCount(Scope s,IntPtr window)
    {
        // Safari's tab bar is AXOpaqueProviderGroup, not AXTabGroup. Tab identifiers
        // describe flags and are shared by many tabs, so never deduplicate them.
        var bar=s.Scan(window,node=>{var id=s.Txt(node,"AXIdentifier");return id=="TabBar"||id.StartsWith("TabBar?",StringComparison.Ordinal);},450,1,true);
        if(bar!=IntPtr.Zero)return s.Array(s.Attr(bar,"AXChildren")).Count(child=>s.Txt(child,"AXRole") is "AXRadioButton" or "AXTab");
        var count=0;
        s.Scan(window,node=>{
            if(s.Txt(node,"AXRole")=="AXTabGroup")count=Math.Max(count,s.Array(s.Attr(node,"AXChildren")).Count(child=>s.Txt(child,"AXRole") is "AXRadioButton" or "AXTab"));
            return false;
        },450,1,true);
        return count;
    }
    internal static void SaveMeetCapabilities()
    {
        using var s=new Scope();var report=new List<object>();
        foreach(var bundle in Browsers){
            var running=s.Array(SendP(Class("NSRunningApplication"),Sel("runningApplicationsWithBundleIdentifier:"),s.Str(bundle))).FirstOrDefault();
            if(running==IntPtr.Zero)continue;
            var app=new App((int)Get(running,"processIdentifier"),bundle);var window=s.Window(app);var field=Address(s,app,window);
            var loadedMeetOrigin=MeetOrigin(LoadedUrl(s,window));var controls=new List<object>();
            if(loadedMeetOrigin){
                var web=s.Scan(window,node=>s.Txt(node,"AXRole")=="AXWebArea",300,.25);
                s.Scan(web,node=>{
                    var label=s.Label(node).Trim();
                    foreach(var term in new[]{"new meeting","start an instant meeting"})if(label==term)controls.Add(new{control=term,role=s.Txt(node,"AXRole"),press=s.HasAction(node,"AXPress")});
                    return false;
                },900,.45);
            }
            report.Add(new{browser=bundle,tabs=TabCount(s,window),addressField=field!=IntPtr.Zero,confirm=field!=IntPtr.Zero&&s.HasAction(field,"AXConfirm"),loadedMeetOrigin,controls,actions=field!=IntPtr.Zero&&AXUIElementCopyActionNames(field,out var actionNames)==0?s.Array(s.Hold(actionNames)).Select(Text).ToArray():System.Array.Empty<string>()});
        }
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","KeypadBrightness");
        Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"meet-capabilities.json"),JsonSerializer.Serialize(new{checkedAt=DateTime.UtcNow,browsers=report}));
    }
    private static string NewMeet(Scope s,App app,CancellationToken cancellation)
    {
        if(!Browsers.Contains(app.Bundle))return "Спочатку відкрий Safari, Firefox або Chrome";
        MeetStep("started");
        var window=s.Window(app);
        var before=TabCount(s,window);
        MeetStep("tab-check",new{before,active=Active(app),sameWindow=SameWindow(s,app,window)});
        if(before<1||!SameWindow(s,app,window)||cancellation.IsCancellationRequested)return "Не вдалося перевірити вкладки цього вікна";
        Key(17,Cmd);
        var newTab=false;
        for(var i=0;i<25&&!cancellation.IsCancellationRequested;i++){
            if(!SameWindow(s,app,window))return "Вікно змінилося · Meet зупинено";
            var after=TabCount(s,window);
            MeetStep("waiting-new-tab",new{before,after});
            if(after==before+1){newTab=true;break;}
            if(after>before+1)return "Кілька вкладок змінилися · Meet зупинено";
            Thread.Sleep(120);
        }
        if(!newTab)return "Нова вкладка не підтверджена · перехід скасовано";
        MeetStep("tab-created");
        // A new tab must focus its own address bar. Do not send Cmd-L or guess focus.
        IntPtr field=IntPtr.Zero;
        for(var i=0;i<15&&!cancellation.IsCancellationRequested;i++){
            if(!SameWindow(s,app,window))return "Вікно змінилося · Meet зупинено";
            field=Address(s,app,window);var focused=Focused(s,app);
            MeetStep("address-focus",new{fieldFound=field!=IntPtr.Zero,focusedRole=s.Txt(focused,"AXRole"),focusedIdentifier=s.Txt(focused,"AXIdentifier"),equal=field!=IntPtr.Zero&&focused!=IntPtr.Zero&&CFEqual(field,focused)!=0});
            if(field!=IntPtr.Zero&&focused!=IntPtr.Zero&&CFEqual(field,focused)!=0)break;
            field=IntPtr.Zero;Thread.Sleep(100);
        }
        if(field==IntPtr.Zero||cancellation.IsCancellationRequested||!s.SetText(field,MeetHome)||!AddressReady(s,app,window,field))return "Не вдалося перевірити точну адресу · перехід скасовано";
        MeetStep("address-verified");
        Thread.Sleep(120);
        if(cancellation.IsCancellationRequested||!AddressReady(s,app,window,field))return "Адреса або фокус змінилися · перехід скасовано";
        // Confirm the exact address using the supported browser-specific route.
        // Never send a URL string to KeyboardApi: it drops punctuation and queues characters.
        if(!ConfirmAddress(s,app,window,field,cancellation))return "Браузер не підтримує безпечне відкриття адреси";
        MeetStep("navigation-sent");
        var timer=Stopwatch.StartNew();var stage=0;
        while(timer.Elapsed.TotalSeconds<18&&!cancellation.IsCancellationRequested){
            if(!SameWindow(s,app,window))return "Вікно змінилося · Meet зупинено";
            var url=LoadedUrl(s,window);
            if(url.Length==0||url is "about:blank" or "about:newtab" or "about:home"){Thread.Sleep(150);continue;}
            if(!MeetOrigin(url))return url.StartsWith("https://accounts.google.com/",StringComparison.Ordinal)?"Увійди в Google Meet у цій вкладці":"Неочікувана адреса · автоматизацію зупинено";
            MeetStep("meet-origin-verified",new{stage});
            var meeting=MeetingUrl(url);
            if(meeting!=null)return Copy(s,meeting)?"Meet відкрито · посилання у буфері":"Meet відкрито · не вдалося скопіювати посилання";
            var terms=stage==0?new[]{"new meeting","new ","нова зустріч","створити зустріч","нова ","новая встреча"}:new[]{"start an instant meeting","start instant meeting","почати миттєву зустріч","розпочати миттєву зустріч","начать мгновенную встречу","начать встречу прямо сейчас"};
            var web=s.Scan(window,node=>s.Txt(node,"AXRole")=="AXWebArea",300,.25);
            var button=stage<2?s.Scan(web,node=>(s.Txt(node,"AXRole") is "AXButton" or "AXPopUpButton" or "AXMenuItem")&&terms.Any(term=>s.Label(node).Trim().StartsWith(term,StringComparison.Ordinal)),900,.45):IntPtr.Zero;
            if(button!=IntPtr.Zero){
                // Re-check loaded origin and active window immediately before every page action.
                if(!SameWindow(s,app,window)||!MeetOrigin(LoadedUrl(s,window))||cancellation.IsCancellationRequested)return "Сторінка змінилася · Meet зупинено";
                if(!s.Press(button))return "Не вдалося натиснути кнопку Meet";
                stage++;MeetStep("meeting-control-pressed",new{stage});
            }
            Thread.Sleep(200);
        }
        return cancellation.IsCancellationRequested?"Meet зупинено":"Meet відкрито · заверши створення зустрічі у браузері";
    }
    private static IntPtr StopRecording(Scope s)
    {
        foreach(var bundle in new[]{"com.apple.screencaptureui","com.apple.controlcenter"})
        foreach(var app in s.Array(SendP(Class("NSRunningApplication"),Sel("runningApplicationsWithBundleIdentifier:"),s.Str(bundle))))
        {
            var root=s.Hold(AXUIElementCreateApplication((int)Get(app,"processIdentifier")));
            foreach(var name in new[]{"AXMenuBar","AXExtrasMenuBar"}){
                var stop=s.Scan(s.Element(root,name),node=>{
                    var role=s.Txt(node,"AXRole");
                    return (role is "AXMenuBarItem" or "AXButton" or "AXMenuItem")&&new[]{"stop recording","stop screen recording","зупинити запис","остановить запись"}.Any(s.Label(node).Contains);
                },70,.15);
                if(stop!=IntPtr.Zero)return stop;
            }
        }
        return IntPtr.Zero;
    }
    private static IntPtr CallWindow(Scope s,App app)
    {
        var focused=s.Window(app);
        if(app?.Bundle=="us.zoom.xos")return ZoomCallWindow(s,app);
        if(app?.Bundle!="com.tinyspeck.slackmacgap")return focused;
        // A composer microphone is not a huddle control. Require an actual huddle.
        var windows=new[]{focused}.Concat(s.Array(s.Attr(s.Root(app),"AXWindows"))).Where(p=>p!=IntPtr.Zero).Distinct().Take(5);
        foreach(var window in windows)
            if(s.Scan(window,node=>s.Txt(node,"AXRole")=="AXButton"&&new[]{"leave huddle","exit huddle","leave call","вийти з розмови","покинути розмову"}.Any(s.Label(node).Contains),500,.25)!=IntPtr.Zero)return window;
        return IntPtr.Zero;
    }
    internal static bool? CallLabelState(string action,string label)
    {
        label=Regex.Replace(label??"",@"\s+"," ").Trim().ToLowerInvariant();
        bool Match(string pattern)=>Regex.IsMatch(label,pattern);
        if(action=="mic"){
            if(Match(@"^mute (?:all|participants|others|media|this tab)\b"))return null;
            if(label.Contains("currently unmuted"))return true;
            if(label.Contains("currently muted"))return false;
            if(Match(@"^(unmute\b|turn on (?:your |the )?microphone\b|увімкнути мікрофон\b|включить микрофон\b)"))return false;
            if(Match(@"^(mute(?! all\b| participants\b| others\b)\b|turn off (?:your |the )?microphone\b|вимкнути мікрофон\b|отключить микрофон\b)"))return true;
        }else if(action=="camera"){
            if(label.Contains("video, ")&&label.Contains("currently on"))return true;
            if(label.Contains("video, ")&&label.Contains("currently off"))return false;
            if(Match(@"^(start video\b|turn on (?:your |the )?(?:camera|video)\b|turn camera on\b|увімкнути камер[уа]\b|включить камер[уы]\b)"))return false;
            if(Match(@"^(stop video\b|turn off (?:your |the )?(?:camera|video)\b|turn camera off\b|вимкнути камер[уа]\b|отключить камер[уы]\b)"))return true;
        }else if(action=="share"){
            if(Match(@"^(stop (?:sharing|share|presenting)\b|зупинити (?:показ|демонстрацію)\b|остановить (?:демонстрацию|показ)\b|you(?: are|'re) presenting\b)"))return true;
            if(Match(@"^(share screen\b|start share\b|present now\b|показати екран\b|демонстрація екрана\b)"))return false;
        }
        return null;
    }
    private static Dictionary<string,(IntPtr Button,bool? On)> CallControls(Scope s,App app,IntPtr window)
    {
        var found=new Dictionary<string,(IntPtr Button,bool? On)>{{"mic",(IntPtr.Zero,null)},{"camera",(IntPtr.Zero,null)},{"share",(IntPtr.Zero,null)}};
        if(window==IntPtr.Zero)return found;
        var zoom=app.Bundle=="us.zoom.xos";
        var root=zoom?s.Element(s.Root(app),"AXMenuBar"):app.Bundle=="com.tinyspeck.slackmacgap"?window:s.Scan(window,n=>s.Txt(n,"AXRole")=="AXWebArea",300,.3);
        // One bounded walk of the call surface. Device-setting buttons and toolbar
        // capture indicators are not toggles, and cannot replace actual call state.
        s.Scan(root,node=>{
            if(s.Txt(node,"AXRole") is not ("AXButton" or "AXCheckBox" or "AXMenuButton" or "AXPopUpButton" or "AXMenuItem"))return false;
            var id=zoom?s.Txt(node,"AXIdentifier"):"";var label=s.Label(node);
            foreach(var action in new[]{"mic","camera","share"}){
                if(zoom&&id!=(action=="mic"?"onMuteAudio:":action=="camera"?"onMuteVideo:":"onShare:"))continue;
                var on=CallLabelState(action,label);if(!on.HasValue)continue;
                var candidate=found[action];
                if(candidate.Button==IntPtr.Zero||action=="share"&&on==true&&(candidate.On!=true||ExplicitShareStop(label)&&!ExplicitShareStop(s.Label(candidate.Button))))found[action]=(node,on);
            }
            return false;
        },900,.9);
        return found;
    }
    internal static bool ExplicitShareStop(string label)=>Regex.IsMatch((label??"").Trim().ToLowerInvariant(),@"^(stop (?:sharing|share|presenting)\b|зупинити (?:показ|демонстрацію)\b|остановить (?:демонстрацию|показ)\b)");
    private static IntPtr ZoomCallWindow(Scope s,App app)
    {
        var windows=new[]{s.Window(app)}.Concat(s.Array(s.Attr(s.Root(app),"AXWindows"))).Where(p=>p!=IntPtr.Zero);
        return windows.FirstOrDefault(window=>s.Txt(window,"AXIdentifier")=="zm.meeting.window.main");
    }
    internal static void SaveCallCapabilities()
    {
        using var s=new Scope();var report=new List<object>();
        foreach(var bundle in new[]{"com.apple.Safari","org.mozilla.firefox","com.tinyspeck.slackmacgap","us.zoom.xos"}){
            var running=s.Array(SendP(Class("NSRunningApplication"),Sel("runningApplicationsWithBundleIdentifier:"),s.Str(bundle))).FirstOrDefault();
            if(running==IntPtr.Zero)continue;
            var app=new App((int)Get(running,"processIdentifier"),bundle);var window=CallWindow(s,app);
            if(Browsers.Contains(bundle)&&MeetingUrl(LoadedUrl(s,window))==null)continue;
            var controls=CallControls(s,app,window);
            report.Add(new{app=bundle,controls=controls.Select(c=>new{action=c.Key,on=c.Value.On,found=c.Value.Button!=IntPtr.Zero,press=s.HasAction(c.Value.Button,"AXPress"),role=s.Txt(c.Value.Button,"AXRole")})});
        }
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","KeypadBrightness");
        Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"call-capabilities.json"),JsonSerializer.Serialize(new{checkedAt=DateTime.UtcNow,apps=report}));
    }
    internal static DashboardSnapshot Snapshot()
    {
        using var s=new Scope();var app=Current();var bundle=app?.Bundle??"";var trusted=AXIsProcessTrusted()!=0;
        var context=bundle.StartsWith("com.prusa3d.slic3r")?"prusa":bundle=="com.apple.dt.Xcode"?"xcode":bundle=="com.tinyspeck.slackmacgap"?"slack":bundle=="us.zoom.xos"?"zoom":Browsers.Contains(bundle)?(trusted&&MeetingUrl(LoadedUrl(s,s.Window(app)))!=null?"meet":"browser"):"general";
        var recording=trusted&&StopRecording(s)!=IntPtr.Zero;
        if(recording)recordingBegan??=DateTime.UtcNow;else recordingBegan=null;
        bool? mic=null,camera=null,sharing=null;var preview=false;
        if(trusted&&context is "slack" or "meet" or "zoom"){
            var window=CallWindow(s,app);var controls=CallControls(s,app,window);
            var key=window==IntPtr.Zero?"":app.Pid+":"+s.Txt(window,"AXIdentifier")+":"+(context=="meet"?LoadedUrl(s,window):context);
            (mic,camera,sharing)=callMemory.Merge(key,DateTime.UtcNow,controls["mic"].On,controls["camera"].On,controls["share"].On);
        }else callMemory.Clear();
        if(trusted&&context=="prusa"){
            var root=s.Window(app);
            preview=s.Scan(root,node=>(s.Txt(node,"AXRole") is "AXRadioButton" or "AXTab")&&s.Label(node).Contains("preview")&&(s.IsTrue(node,"AXSelected")||s.IsTrue(node,"AXValue")),300,.2)!=IntPtr.Zero;
        }
        return new(context,recording,recordingBegan.HasValue?(int)(DateTime.UtcNow-recordingBegan.Value).TotalSeconds:0,mic,camera,sharing,trusted,preview);
    }
    private static void Key(ushort code,ulong flags=0)
    {
        var key=code switch {17=>VirtualKeyCode.KeyT,37=>VirtualKeyCode.KeyL,36=>VirtualKeyCode.Return,23=>VirtualKeyCode.Key5,21=>VirtualKeyCode.Key4,29=>VirtualKeyCode.Key0,22=>VirtualKeyCode.Key6,18=>VirtualKeyCode.Key1,48=>VirtualKeyCode.Tab,_=>VirtualKeyCode.None};
        if(key==VirtualKeyCode.None)throw new InvalidOperationException("Unsupported shortcut");
        var modifiers=ModifierKey.None;if((flags&Cmd)!=0)modifiers|=ModifierKey.Command;if((flags&Shift)!=0)modifiers|=ModifierKey.Shift;if((flags&Ctrl)!=0)modifiers|=ModifierKey.Control;
        MotorControlsPlugin.Current.KeyboardApi.SendShortcut(key,modifiers);
    }
    private static bool Copy(Scope s,string value){var pb=Get(Class("NSPasteboard"),"generalPasteboard");Get(pb,"clearContents");return SendPP(pb,Sel("setString:forType:"),s.Str(value),s.Str("public.utf8-plain-text"))!=IntPtr.Zero;}
    private static string LastCapture(Scope s)
    {
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);var roots=new List<string>{Path.Combine(home,"Desktop")};
        var defaults=SendP(Get(Class("NSUserDefaults"),"alloc"),Sel("initWithSuiteName:"),s.Str("com.apple.screencapture"));
        var location=Text(SendP(defaults,Sel("stringForKey:"),s.Str("location")));Get(defaults,"release");
        if(location.Length>0)roots.Insert(0,location.StartsWith("~/")?Path.Combine(home,location[2..]):location);
        return roots.Distinct().Where(Directory.Exists).SelectMany(root=>Directory.EnumerateFiles(root)).Where(file=>new[]{".png",".jpg",".jpeg",".heic",".mov",".mp4"}.Contains(Path.GetExtension(file).ToLowerInvariant())&&new[]{"screenshot","screen shot","screen recording","знімок екрана","запис екрана","снимок экрана","запись экрана"}.Any(Path.GetFileName(file).ToLowerInvariant().Contains)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
    internal static string Execute(string action,CancellationToken cancellation=default)
    {
        using var s=new Scope();if(AXIsProcessTrusted()==0)return "Потрібен доступ LogiPluginService до Accessibility";
        if(action=="capture"){var stop=StopRecording(s);if(stop!=IntPtr.Zero)return s.Press(stop)?"Запис зупинено":"Не вдалося зупинити запис";var start=new ProcessStartInfo("/usr/bin/open"){UseShellExecute=false};start.ArgumentList.Add("-a");start.ArgumentList.Add("/System/Applications/Utilities/Screenshot.app");Process.Start(start)?.Dispose();return "Панель знімка / запису";}
        if(action=="context"){Key(21,Cmd|Shift|Ctrl);return "Обери область · знімок буде в буфері";}
        if(action=="last"){var file=LastCapture(s);if(file==null)return "Збережених знімків або записів не знайдено";var url=SendP(Class("NSURL"),Sel("fileURLWithPath:"),s.Str(file));return SendP(Workspace(),Sel("openURL:"),url)!=IntPtr.Zero?"Останній знімок відкрито":"Не вдалося відкрити знімок";}
        var app=Current();if(!Active(app))return "Повернись у робочий застосунок";
        if(action.StartsWith("prusa-")&&app.Bundle.StartsWith("com.prusa3d.slic3r")){var focused=s.Element(s.Hold(AXUIElementCreateSystemWide()),"AXFocusedUIElement");if(s.Txt(focused,"AXRole") is "AXTextField" or "AXTextArea" or "AXComboBox")return "Спочатку обери 3D-сцену";ushort code=action switch{"prusa-default"=>29,"prusa-left"=>23,"prusa-right"=>22,"prusa-top"=>18,"prusa-preview"=>48,_=>65535};if(code==65535)return "Невідома дія";Key(code);return "Вид змінено";}
        if(action=="error"&&app.Bundle=="com.apple.dt.Xcode"){var focused=s.Element(s.Hold(AXUIElementCreateSystemWide()),"AXFocusedUIElement");var value=s.Txt(focused,"AXSelectedText");if(s.Txt(focused,"AXSubrole").Contains("Secure")||value.Length==0)return "Виділи текст помилки в Xcode";return Copy(s,value)?"Помилка у буфері":"Не вдалося скопіювати помилку";}
        if(action=="new-meet")return NewMeet(s,app,cancellation);
        if(action is "mic" or "camera" or "share" && (app.Bundle is "com.tinyspeck.slackmacgap" or "us.zoom.xos"||MeetingUrl(LoadedUrl(s,s.Window(app)))!=null)){
            var window=CallWindow(s,app);var control=CallControls(s,app,window)[action];
            if(control.Button==IntPtr.Zero)return "Керування дзвінком зараз недоступне";
            if(!Active(app))return "Повернись у вікно дзвінка";
            if(Browsers.Contains(app.Bundle)&&(!SameWindow(s,app,window)||MeetingUrl(LoadedUrl(s,window))==null))return "Сторінка змінилася · дію скасовано";
            if(cancellation.IsCancellationRequested)return "Дію скасовано";
            if(action=="share"&&control.On==true&&!ExplicitShareStop(s.Label(control.Button)))return "Кнопку зупинки показу не знайдено";
            if(!s.Press(control.Button))return "Не вдалося виконати дію";
            return action=="share"?(control.On==true?"Показ зупинено":"Обери вікно для показу"):"Стан змінено";
        }
        return "Ця дія недоступна в поточному застосунку";
    }
}
