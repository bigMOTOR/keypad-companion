namespace Loupedeck.MotorControlsPlugin;
public abstract class LiveTileCommand : PluginDynamicCommand, IDisposable
{
    private System.Threading.Timer timer;
    private string lastKey;
    protected LiveTileCommand(string title, string description) : base(title, description, "AI Essentials") { IsWidget = true; MotorControlsPlugin.Pollers.Add(this); }
    protected abstract string ImageKey();
    protected override bool OnLoad()
    {
        timer = new System.Threading.Timer(_ => { try { var key = ImageKey(); if (key == lastKey) return; lastKey = key; ActionImageChanged(); } catch { } }, null, 200, 1000);
        return true;
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
    public void Dispose() { timer?.Dispose(); timer = null; }
    private protected static string AgentImage(string brand, AgentView view) => (view.State is "idle" or "stale" or "work") && view.Remaining.HasValue ? $"{brand}-{view.State}-{Math.Clamp(view.Remaining.Value,0,100)}.png" : $"{brand}-{view.State}.png";
}
