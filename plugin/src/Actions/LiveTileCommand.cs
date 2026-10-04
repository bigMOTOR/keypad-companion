namespace Loupedeck.MotorControlsPlugin;
public abstract class LiveTileCommand : PluginDynamicCommand, IDisposable
{
    private System.Threading.Timer timer;
    private string lastKey;
    private int updating, wakeScheduled;
    private long lastUpdate;
    private protected virtual TileGroup Group => TileGroup.All;
    protected virtual int NextRefreshMilliseconds => 60000;
    protected LiveTileCommand(string title, string description) : base(title, description, "AI Essentials") { IsWidget = true; MotorControlsPlugin.Pollers.Add(this); }
    protected abstract string ImageKey();
    protected override bool OnLoad()
    {
        timer=new System.Threading.Timer(_=>Refresh(),null,200,Timeout.Infinite);
        TileSignals.Changed+=Wake;return true;
    }
    private void Wake(TileGroup group)
    {
        if((group&Group)==0||Interlocked.CompareExchange(ref wakeScheduled,1,0)!=0)return;
        try { timer?.Change((int)Math.Clamp(1000-(Environment.TickCount64-Interlocked.Read(ref lastUpdate)),100,1000),Timeout.Infinite); } catch(ObjectDisposedException) { }
    }
    private void Refresh()
    {
        Interlocked.Exchange(ref wakeScheduled,0);
        if(Interlocked.Exchange(ref updating,1)!=0){Wake(Group);return;}
        Interlocked.Exchange(ref lastUpdate,Environment.TickCount64);
        try {
            // Arm before doing work: a notification during the read can bring this forward.
            timer?.Change(Math.Clamp(NextRefreshMilliseconds,100,60000),Timeout.Infinite);
            var key=ImageKey();if(key==lastKey)return;lastKey=key;ActionImageChanged();
        } catch { } finally { Interlocked.Exchange(ref updating,0); }
    }
    protected override string GetCommandDisplayName(string actionParameter, PluginImageSize imageSize) => "";
    private string failedImage;
    protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
    {
        var key=ImageKey();
        try { return PluginResources.ReadImage(key); }
        catch(Exception e) { if(failedImage!=key){failedImage=key;PluginLog.Error("Tile image "+key+": "+e.GetType().Name+" "+e.Message);} return PluginResources.ReadImage("gpt-unknown.png"); }
    }
    protected override bool OnUnload() { Dispose(); return true; }
    public void Dispose() { TileSignals.Changed-=Wake;timer?.Dispose();timer=null; }
    private protected static string AgentImage(string brand, AgentView view) => (view.State is "idle" or "stale" or "work") && view.Remaining.HasValue ? $"{brand}-{view.State}-{Math.Clamp(view.Remaining.Value,0,100)}.png" : $"{brand}-{view.State}.png";
}
