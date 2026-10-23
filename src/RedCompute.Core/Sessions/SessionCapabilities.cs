namespace RedCompute.Core.Sessions;

[Flags]
public enum SessionCapabilities
{
    None = 0,
    StatelessExecution = 1 << 0,
    PersistentSessions = 1 << 1,
    Resume = 1 << 2,
    Interrupt = 1 << 3,
    SendMessage = 1 << 4,
    PermissionMode = 1 << 5,
    ImageAttachments = 1 << 7,
    Generate = 1 << 9,
    FileAttachments = 1 << 10,
    ImageOutputs = 1 << 11,
    /// <summary>
    /// Stateless generation can run with an enforced empty tool surface that the provider verifies
    /// for each call before returning model text, at least when the request sets requireToolFree.
    /// Such a call reports <see cref="SessionIsolation.VerifiedToolFree"/>.
    /// </summary>
    ToolFreeGenerate = 1 << 12,
    /// <summary>Injects user input into the currently active provider turn without interrupting it.</summary>
    SupportsSteering = 1 << 13,
    /// <summary>Stops one running background task (subagent or background shell) without interrupting the session.</summary>
    SupportsBackgroundTaskStop = 1 << 14,
    /// <summary>
    /// Stateless generation accepts validated image inputs (<see cref="SessionGenerateRequest.Images"/>)
    /// and delivers them to the model, including on a verified tool-free call.
    /// </summary>
    GenerateVision = 1 << 15,
}
