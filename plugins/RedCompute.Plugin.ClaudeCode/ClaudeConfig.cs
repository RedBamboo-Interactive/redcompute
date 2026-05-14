namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeConfig
{
    public string ProjectsRoot { get; set; } = @"T:\Projects";
    public string? ClaudePath { get; set; }
    public int MaxSessions { get; set; } = 5;
    public string? Model { get; set; }
    public string DefaultOneshotModel { get; set; } = "haiku";
}
