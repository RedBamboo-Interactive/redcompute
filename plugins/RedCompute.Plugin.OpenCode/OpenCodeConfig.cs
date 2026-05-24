namespace RedCompute.Plugin.OpenCode;

public class OpenCodeConfig
{
    public string ProjectsRoot { get; set; } = @"T:\Projects";
    public string? OpenCodePath { get; set; }
    public int MaxSessions { get; set; } = 99;
    public string? Model { get; set; }
    public string DefaultModel { get; set; } = "sonnet";
}
