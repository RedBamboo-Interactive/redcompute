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
    /// Stateless generation starts with an enforced empty tool surface and verifies that surface
    /// from the provider runtime before returning model text.
    /// </summary>
    ToolFreeGenerate = 1 << 12,
}
