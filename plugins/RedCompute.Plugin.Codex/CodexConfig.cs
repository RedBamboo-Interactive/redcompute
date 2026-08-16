namespace RedCompute.Plugin.Codex;

public class CodexConfig
{
    public string? CodexPath { get; set; }
    public int MaxSessions { get; set; } = 99;
    public string? Model { get; set; }
    public string SandboxMode { get; set; } = "workspace-write";
}
