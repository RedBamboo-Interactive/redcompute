namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeConfig
{
    public string? ClaudePath { get; set; }
    public int MaxSessions { get; set; } = 99;
    public string? Model { get; set; }
}
