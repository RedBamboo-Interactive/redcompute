namespace RedCompute.Plugin.Codex;

public class CodexConfig
{
    public string ProjectsRoot { get; set; } = @"T:\Projects";
    public string? CodexPath { get; set; }
    public int MaxSessions { get; set; } = 99;
    public string? Model { get; set; }
    public string DefaultExecModel { get; set; } = "codex-mini-latest";
    public string SandboxMode { get; set; } = "workspace-write";
}
