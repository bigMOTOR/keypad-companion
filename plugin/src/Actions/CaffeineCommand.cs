namespace Loupedeck.MotorControlsPlugin;
public class CaffeineCommand : LiveTileCommand
{
    public CaffeineCommand() : base("Caffeinate", "Keep Mac and display awake for the duration chosen in Keypad settings, with remaining hours and minutes.") { }
    protected override void RunCommand(string actionParameter) { CaffeinateController.Instance.Toggle(); ActionImageChanged(); }
    protected override string ImageKey()
    {
        var state = CaffeinateController.Instance.State;
        return state.Error != null ? "caffeine-error.png" : state.On ? $"caffeine-{Math.Clamp(state.Minutes, 1, 1440)}-{Math.Round(state.Fraction*100)}" : "caffeine-off.png";
    }
    protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
    {
        var state = CaffeinateController.Instance.State;
        if (state.Error != null || !state.On) return PluginResources.ReadImage(state.Error != null ? "caffeine-error.png" : "caffeine-off.png");
        using var builder = new BitmapBuilder(90,90);
        builder.DrawImage(PluginResources.ReadImage($"caffeine-{Math.Clamp(state.Minutes,1,1440)}.png"),0,0);
        builder.DrawImage(PluginResources.ReadImage($"caffeine-progress-{(int)Math.Round(state.Fraction*100)}.png"),0,0);
        return builder.ToImage();
    }
}
