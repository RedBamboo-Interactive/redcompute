namespace RedCompute.Plugin.OpenCode;

public class OpenCodeConfig
{
    public string? OpenCodePath { get; set; }
    public int MaxSessions { get; set; } = 99;
    public string? Model { get; set; }
}
