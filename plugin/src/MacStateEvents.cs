using System.Runtime.InteropServices;
namespace Loupedeck.MotorControlsPlugin;
// Receives macOS workspace/window notifications in the existing plugin process.
// No keystroke listener, new service, network access, or persistent UI data.
internal static class MacStateEvents
{
    private const string ObjC="/usr/lib/libobjc.A.dylib", CF="/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", AX="/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WorkspaceCallback(IntPtr self,IntPtr selector,IntPtr notification);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AxCallback(IntPtr observer,IntPtr element,IntPtr name,IntPtr context);
    private static readonly WorkspaceCallback workspaceCallback=WorkspaceChanged;
    private static readonly AxCallback axCallback=AxChanged;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TimerCallback(IntPtr timer,IntPtr context);
    private static readonly TimerCallback heartbeat=(_,_)=>{};
    private static Thread worker;
    private static volatile bool stopping;
    private static int rebind;
    private static long workspaceEvents,windowEvents;
    internal static object Diagnostics=>new { listening=loop!=IntPtr.Zero,workspaceEvents=Interlocked.Read(ref workspaceEvents),windowEvents=Interlocked.Read(ref windowEvents) };
    private static IntPtr loop,center,listener,observer,root,window,keepAlive;
    private static int observedPid;
    private static DateTime captureUntil;
    internal static bool CapturePending => DateTime.UtcNow<captureUntil;
    internal static void TrackCapture()=>captureUntil=DateTime.UtcNow.AddMinutes(2);
    internal static void Start(){if(!OperatingSystem.IsMacOS()||worker!=null)return;stopping=false;worker=new Thread(Listen){IsBackground=true,Name="Keypad state events"};worker.Start();}
    internal static void Stop(){stopping=true;var l=loop;if(l!=IntPtr.Zero)CFRunLoopStop(l);worker?.Join(2000);worker=null;}
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr parent,string name,nuint size);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] [return:MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls,IntPtr selector,IntPtr implementation,string types);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern IntPtr Send(IntPtr receiver,IntPtr selector);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern IntPtr SendP(IntPtr receiver,IntPtr selector,IntPtr value);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern void Add(IntPtr receiver,IntPtr selector,IntPtr listener,IntPtr callback,IntPtr name,IntPtr obj);
    [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr p);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,string value,uint encoding);
    [DllImport(CF)] private static extern void CFRelease(IntPtr value);
    [DllImport(CF)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CF)] private static extern int CFRunLoopRunInMode(IntPtr mode,double seconds,[MarshalAs(UnmanagedType.I1)] bool returnAfterSource);
    [DllImport(CF)] private static extern void CFRunLoopAddSource(IntPtr loop,IntPtr source,IntPtr mode);
    [DllImport(CF)] private static extern void CFRunLoopRemoveSource(IntPtr loop,IntPtr source,IntPtr mode);
    [DllImport(CF)] private static extern void CFRunLoopStop(IntPtr loop);
    [DllImport(CF)] private static extern double CFAbsoluteTimeGetCurrent();
    [DllImport(CF)] private static extern IntPtr CFRunLoopTimerCreate(IntPtr allocator,double fireDate,double interval,nuint flags,nint order,TimerCallback callback,IntPtr context);
    [DllImport(CF)] private static extern void CFRunLoopAddTimer(IntPtr loop,IntPtr timer,IntPtr mode);
    [DllImport(CF)] private static extern void CFRunLoopTimerInvalidate(IntPtr timer);
    [DllImport(AX)] private static extern int AXObserverCreate(int pid,AxCallback callback,out IntPtr observer);
    [DllImport(AX)] private static extern IntPtr AXObserverGetRunLoopSource(IntPtr observer);
    [DllImport(AX)] private static extern int AXObserverAddNotification(IntPtr observer,IntPtr element,IntPtr name,IntPtr context);
    [DllImport(AX)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(AX)] private static extern int AXUIElementCopyAttributeValue(IntPtr element,IntPtr name,out IntPtr value);
    [DllImport(AX)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element,float seconds);
    private static IntPtr Selector(string value)=>sel_registerName(value);
    private static IntPtr Get(IntPtr target,string selector)=>Send(target,Selector(selector));
    private static IntPtr Str(string text)=>CFStringCreateWithCString(IntPtr.Zero,text,0x08000100);
    private static void WorkspaceChanged(IntPtr self,IntPtr selector,IntPtr notification)
    {
        try{
            Interlocked.Increment(ref workspaceEvents);
            var key=Str("NSWorkspaceApplicationKey");
            try{var app=SendP(Get(notification,"userInfo"),Selector("objectForKey:"),key);var bundle=Text(Get(app,"bundleIdentifier"));if(bundle is "com.apple.screencaptureui" or "com.apple.screenshot.launcher")TrackCapture();}finally{CFRelease(key);}
            Interlocked.Exchange(ref rebind,1);Dashboard.Invalidate();var l=loop;if(l!=IntPtr.Zero)CFRunLoopStop(l);
        }catch{ }
    }
    private static void AxChanged(IntPtr sender,IntPtr element,IntPtr name,IntPtr context)
    {
        try{Interlocked.Increment(ref windowEvents);Dashboard.Invalidate();if(Text(name) is "AXFocusedWindowChanged" or "AXWindowCreated")Interlocked.Exchange(ref rebind,1);}catch{ }
    }
    [DllImport(CF)] private static extern byte CFStringGetCString(IntPtr value,byte[] buffer,nint size,uint encoding);
    private static string Text(IntPtr value){if(value==IntPtr.Zero)return "";var bytes=new byte[512];if(CFStringGetCString(value,bytes,bytes.Length,0x08000100)==0)return "";var end=Array.IndexOf(bytes,(byte)0);return System.Text.Encoding.UTF8.GetString(bytes,0,end<0?bytes.Length:end);}
    private static IntPtr mode;
    private static void Listen()
    {
        try {
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            loop=CFRunLoopGetCurrent();mode=Str("kCFRunLoopDefaultMode");
            keepAlive=CFRunLoopTimerCreate(IntPtr.Zero,CFAbsoluteTimeGetCurrent()+60,60,0,0,heartbeat,IntPtr.Zero);
            CFRunLoopAddTimer(loop,keepAlive,mode);
            var pool=objc_autoreleasePoolPush();
            try{
                var cls=objc_allocateClassPair(objc_getClass("NSObject"),"MotorKeypadEvents_"+Guid.NewGuid().ToString("N"),0);
                if(cls==IntPtr.Zero||!class_addMethod(cls,Selector("keypadChanged:"),Marshal.GetFunctionPointerForDelegate(workspaceCallback),"v@:@"))throw new InvalidOperationException("Workspace observer unavailable");
                objc_registerClassPair(cls);listener=Get(Get(cls,"alloc"),"init");
                center=Get(Get(objc_getClass("NSWorkspace"),"sharedWorkspace"),"notificationCenter");
                foreach(var name in new[]{"NSWorkspaceDidActivateApplicationNotification","NSWorkspaceDidLaunchApplicationNotification","NSWorkspaceDidTerminateApplicationNotification","NSWorkspaceDidWakeNotification"}){
                    var n=Str(name);try{Add(center,Selector("addObserver:selector:name:object:"),listener,Selector("keypadChanged:"),n,IntPtr.Zero);}finally{CFRelease(n);}
                }
            }finally{objc_autoreleasePoolPop(pool);}
            while(!stopping){
                var pool2=objc_autoreleasePoolPush();try{BindForeground();}finally{objc_autoreleasePoolPop(pool2);}
                CFRunLoopRunInMode(mode,60,true);
            }
        }catch(Exception e){PluginLog.Error("State notifications: "+e.GetType().Name);}
        finally{
            if(center!=IntPtr.Zero&&listener!=IntPtr.Zero)SendP(center,Selector("removeObserver:"),listener);
            ClearAx();if(keepAlive!=IntPtr.Zero){CFRunLoopTimerInvalidate(keepAlive);CFRelease(keepAlive);keepAlive=IntPtr.Zero;}if(listener!=IntPtr.Zero)Get(listener,"release");listener=IntPtr.Zero;center=IntPtr.Zero;loop=IntPtr.Zero;if(mode!=IntPtr.Zero)CFRelease(mode);mode=IntPtr.Zero;
        }
    }
    private static void Subscribe(IntPtr target,params string[] names)
    {
        if(target==IntPtr.Zero)return;
        foreach(var name in names){var text=Str(name);try{AXObserverAddNotification(observer,target,text,IntPtr.Zero);}finally{CFRelease(text);}}
    }
    private static void BindForeground()
    {
        var app=Get(Get(objc_getClass("NSWorkspace"),"sharedWorkspace"),"frontmostApplication");var pid=(int)Get(app,"processIdentifier");
        var dirty=Interlocked.Exchange(ref rebind,0)!=0;
        if(pid==observedPid&&observer!=IntPtr.Zero&&!dirty)return;
        ClearAx();observedPid=pid;
        var bundle=Text(Get(app,"bundleIdentifier"));
        if(!(bundle is "com.apple.Safari" or "org.mozilla.firefox" or "com.tinyspeck.slackmacgap" or "us.zoom.xos" or "com.apple.screencaptureui" or "com.apple.screenshot.launcher"||bundle.StartsWith("com.prusa3d.slic3r")))return;
        if(pid<=1||AXObserverCreate(pid,axCallback,out observer)!=0){observer=IntPtr.Zero;return;}
        root=AXUIElementCreateApplication(pid);AXUIElementSetMessagingTimeout(root,.06f);
        Subscribe(root,"AXFocusedWindowChanged","AXWindowCreated","AXFocusedUIElementChanged");
        var name=Str("AXFocusedWindow");try{if(AXUIElementCopyAttributeValue(root,name,out window)!=0)window=IntPtr.Zero;}finally{CFRelease(name);}
        Subscribe(window,"AXTitleChanged","AXLayoutChanged","AXSelectedChildrenChanged","AXValueChanged","AXUIElementDestroyed");
        CFRunLoopAddSource(loop,AXObserverGetRunLoopSource(observer),mode);
    }
    private static void ClearAx()
    {
        if(observer!=IntPtr.Zero){CFRunLoopRemoveSource(loop,AXObserverGetRunLoopSource(observer),mode);CFRelease(observer);observer=IntPtr.Zero;}
        if(root!=IntPtr.Zero){CFRelease(root);root=IntPtr.Zero;}if(window!=IntPtr.Zero){CFRelease(window);window=IntPtr.Zero;}
    }
}
