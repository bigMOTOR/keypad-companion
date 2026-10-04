namespace Loupedeck.MotorControlsPlugin;
public class ClaudeCommand : LiveTileCommand
{
    public ClaudeCommand() : base("Claude", "Focus the Code session needing attention, running, or recently used; live status and weekly allowance when provided by Code.") { }
    protected override void RunCommand(string actionParameter) => AgentBridge.FocusClaude();
    protected override string ImageKey() => AgentImage("claude", AgentBridge.Claude());
}
