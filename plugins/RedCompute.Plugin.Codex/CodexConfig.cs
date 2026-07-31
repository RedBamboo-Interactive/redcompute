namespace RedCompute.Plugin.Codex;

public class CodexConfig
{
    public string ProjectsRoot { get; set; } = @"T:\Projects";
    public string? CodexPath { get; set; }
    public int MaxSessions { get; set; } = 99;
    public string? Model { get; set; }

    /// <summary>
    /// Optional model override for stateless exec. Null means "let the catalog/CLI decide" — which
    /// is the right default, since the account's own default model is resolved server-side and
    /// changes as OpenAI ships new tiers. Pinning a literal here is how the old hardcoded list
    /// ended up naming six models that no longer exist.
    /// </summary>
    public string? DefaultExecModel { get; set; }

    /// <summary>
    /// Model used for the one-shot that names a new session. Should mirror the "fast" quality tier
    /// — this runs once per session and wants to be the cheapest thing that can write six sensible
    /// words. When unset, titles fall back to being derived from the opening message rather than
    /// silently costing a flagship call.
    /// </summary>
    public string? TitleModel { get; set; }
    public string SandboxMode { get; set; } = "workspace-write";
}
