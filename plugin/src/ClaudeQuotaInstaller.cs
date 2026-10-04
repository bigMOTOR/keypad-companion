using System.Text.Json.Nodes;
namespace Loupedeck.MotorControlsPlugin;

// Only wraps Logitech's generated command. Never replaces a user's own status line.
internal static class ClaudeQuotaInstaller
{
    private static System.Threading.Timer timer;
    private static FileSystemWatcher watcher;
    private static string lastSettings;
    private static DateTime lastVendorWrite;
    private static readonly object gate = new();
    public static void Start() {
        timer=new(_=>Ensure(),null,3000,60000);
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
        if(Directory.Exists(dir)){
            watcher=new(dir,"settings.json"){NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName};
            watcher.Changed+=(_,_)=>Wake();watcher.Created+=(_,_)=>Wake();watcher.Renamed+=(_,_)=>Wake();watcher.Error+=(_,_)=>Wake();watcher.EnableRaisingEvents=true;
        }
    }
    private static void Wake(){try{timer?.Change(500,60000);}catch(ObjectDisposedException){}}
    public static void Stop() { watcher?.Dispose();watcher=null;timer?.Dispose(); timer = null; }
    private static void Ensure()
    {
        if (!OperatingSystem.IsMacOS()) return;
        lock (gate) try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var settings = Path.Combine(home, ".claude", "settings.json");
            if (!File.Exists(settings)) return;
            var original = File.ReadAllText(settings);
            var vendorPath=Path.Combine(home,".claude","claude-desktop","statusline.sh");
            var vendorWrite=File.Exists(vendorPath)?File.GetLastWriteTimeUtc(vendorPath):DateTime.MinValue;
            if(original==lastSettings&&vendorWrite==lastVendorWrite)return;
            var root = JsonNode.Parse(original) as JsonObject;
            var command = root?["statusLine"]?["command"]?.GetValue<string>();
            var vendor = Path.Combine(home, ".claude", "claude-desktop", "statusline.sh");
            var script = Path.Combine(AgentBridge.StateDirectory, "logi-claude-desktop-statusline-motor.py");
            if (command != $"sh '{vendor}'" && command != $"sh \"{vendor}\"" && command != $"/usr/bin/python3 '{script}'") return;
            Directory.CreateDirectory(AgentBridge.StateDirectory);
            var contents=PluginResources.ReadTextFile("logi-claude-desktop-statusline-motor.py");
            if(!File.Exists(script)||File.ReadAllText(script)!=contents)File.WriteAllText(script,contents);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var backup = Path.Combine(AgentBridge.StateDirectory, "claude-settings-before-quota.json");
            if (!File.Exists(backup)) { File.WriteAllText(backup, original); File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            // Existing Code workers may still execute the original script path.
            // Forward that path too, preserving the vendor's generated handler.
            if (File.Exists(vendor))
            {
                var generated = File.ReadAllText(vendor);
                if (generated.Contains("logi-claude-desktop-statusline") && generated.Contains("http://localhost:8765/statusline") && generated.Contains("curl "))
                {
                    var forward = Path.Combine(AgentBridge.StateDirectory, "logi-statusline-forward.sh");
                    File.WriteAllText(forward, generated); File.SetUnixFileMode(forward, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.WriteAllText(vendor, $"#!/bin/sh\n# logi-claude-desktop-statusline — motor allowance forwarder\nexec /usr/bin/python3 '{script}'\n");
                }
            }
            if (command == $"/usr/bin/python3 '{script}'"){lastSettings=original;lastVendorWrite=File.Exists(vendor)?File.GetLastWriteTimeUtc(vendor):DateTime.MinValue;return;}
            root["statusLine"]["command"] = $"/usr/bin/python3 '{script}'";
            var temp = settings + ".motor.tmp";
            File.WriteAllText(temp, root.ToJsonString(new() { WriteIndented = true }));
            File.SetUnixFileMode(temp, File.GetUnixFileMode(settings));
            // Do not overwrite settings edited while the wrapper was being installed.
            if (File.ReadAllText(settings) != original) { File.Delete(temp); return; }
            File.Move(temp, settings, true);lastSettings=File.ReadAllText(settings);lastVendorWrite=File.Exists(vendor)?File.GetLastWriteTimeUtc(vendor):DateTime.MinValue;
        }
        catch { /* Missing integration leaves the tile at 'Немає даних'. */ }
    }
}
