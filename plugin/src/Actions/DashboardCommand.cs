namespace Loupedeck.MotorControlsPlugin;
public abstract class DashboardCommand : LiveTileCommand
{
    private readonly int slot;
    protected DashboardCommand(int slot) : base($"Dashboard {slot + 1}", "Contextual dashboard. Capture stays at the first key of the second row.") { this.slot=slot; }
    protected override void RunCommand(string actionParameter) => Dashboard.Run(slot);
    protected override string ImageKey()
    {
        var tile=Dashboard.Tile(slot); var snapshot=Dashboard.Read();
        return tile.Image+":"+(tile.Image=="capture-stop"?snapshot.Seconds:0)+":"+(Dashboard.Notice(slot)??"")+":"+Dashboard.Busy;
    }
    protected override BitmapImage GetCommandImage(string actionParameter, PluginImageSize imageSize)
    {
        var tile=Dashboard.Tile(slot); var snapshot=Dashboard.Read();
        var resource=PluginResources.ReadImage("dashboard-"+tile.Image+".png");
        var notice=Dashboard.Notice(slot);
        if (tile.Image!="capture-stop" && notice==null) return resource;
        using var builder=new BitmapBuilder(90,90);
        builder.DrawImage(resource,0,0);
        if (notice!=null && tile.Image!="capture-stop") {
            var bg=tile.Image is "capture" or "last" or "context"?new BitmapColor(43,34,60):tile.Image=="error"?new BitmapColor(53,42,32):new BitmapColor(26,39,58);
            builder.FillRectangle(6,67,78,19,bg);
            var shortNotice=notice.Contains("у буфер")?"У буфері":notice.Contains("Виділи")?"Виділи помилку":notice.Contains("Обери область")?"Обери область":notice.Contains("недоступ")||notice.Contains("Не вдалося")||notice.Contains("Потрібен доступ")?"Недоступно":notice.Contains("заверши")?"Заверши в Meet":notice.Contains("Повернись")||notice.Contains("Обери вікно")?"Обери вікно":notice.Contains("не знайдено")?"Немає знімка":"Готово";
            builder.DrawText(shortNotice,6,67,78,19,BitmapColor.White,10);
            return builder.ToImage();
        }
        var span=TimeSpan.FromSeconds(snapshot.Seconds);
        var time=span.TotalHours>=1?$"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}":$"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
        builder.DrawText(time,8,67,74,16,BitmapColor.White,11);
        return builder.ToImage();
    }
}
public class DashboardFirstCommand : DashboardCommand { public DashboardFirstCommand() : base(0) {} }
public class DashboardSecondCommand : DashboardCommand { public DashboardSecondCommand() : base(1) {} }
public class DashboardThirdCommand : DashboardCommand { public DashboardThirdCommand() : base(2) {} }
public class DashboardFourthCommand : DashboardCommand { public DashboardFourthCommand() : base(3) {} }
public class DashboardFifthCommand : DashboardCommand { public DashboardFifthCommand() : base(4) {} }
public class DashboardSixthCommand : DashboardCommand { public DashboardSixthCommand() : base(5) {} }
