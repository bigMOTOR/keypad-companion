namespace Loupedeck.MotorControlsPlugin;
internal static class RefreshPolicy
{
    // Next displayed minute or next integer progress percent, whichever comes first.
    internal static int CoffeeDelay(double remainingSeconds,int totalSeconds)
    {
        if(remainingSeconds<=0||totalSeconds<=0)return 1000;
        var minute=Math.Ceiling(remainingSeconds/60);
        var untilMinute=remainingSeconds-(minute-1)*60;
        var percent=Math.Round(Math.Clamp(remainingSeconds/totalSeconds,0,1)*100);
        var untilProgress=percent>0?remainingSeconds-(percent-.5)*totalSeconds/100:remainingSeconds;
        return (int)Math.Clamp(Math.Ceiling(Math.Max(.1,Math.Min(untilMinute,untilProgress))*1000)+50,100,60000);
    }
}
