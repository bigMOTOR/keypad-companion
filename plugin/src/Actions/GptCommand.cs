namespace Loupedeck.MotorControlsPlugin;
public class GptCommand : LiveTileCommand
{
    public GptCommand() : base("GPT", "Working session, live activity, and remaining weekly Codex allowance.") { }
    protected override void RunCommand(string actionParameter) => AgentBridge.FocusGpt();
    protected override string ImageKey() => AgentImage("gpt", AgentBridge.Gpt());
}
