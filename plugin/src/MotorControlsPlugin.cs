namespace Loupedeck.MotorControlsPlugin
{
    using System;

    // This class contains the plugin-level logic of the Loupedeck plugin.

    public class MotorControlsPlugin : Plugin
    {
        internal static MotorControlsPlugin Current;
        internal static readonly System.Collections.Generic.List<IDisposable> Pollers = new();
        // Gets a value indicating whether this is an API-only plugin.
        public override Boolean UsesApplicationApiOnly => true;

        // Gets a value indicating whether this is a Universal plugin or an Application plugin.
        public override Boolean HasNoApplication => true;

        // Initializes a new instance of the plugin class.
        public MotorControlsPlugin()
        {
            Current = this;
            // Initialize the plugin log.
            PluginLog.Init(this.Log);

            // Initialize the plugin resources.
            PluginResources.Init(this.Assembly);
        }

        // This method is called when the plugin is loaded.
        public override void Load()
        {
            TileFileEvents.Start();
            MacStateEvents.Start();
            ClaudeQuotaInstaller.Start();
            System.Threading.Tasks.Task.Run(()=>{try{MacDashboard.SaveMeetCapabilities();MacDashboard.SaveCallCapabilities();}catch{}});
        }

        // This method is called when the plugin is unloaded.
        public override void Unload()
        {
            MacStateEvents.Stop();
            TileFileEvents.Stop();
            Dashboard.Stop();
            ClaudeQuotaInstaller.Stop();
            foreach (var poller in Pollers) poller.Dispose();
            Pollers.Clear();
            CaffeinateController.Instance.Dispose();
            AgentBridge.Dispose();
        }
    }
}
