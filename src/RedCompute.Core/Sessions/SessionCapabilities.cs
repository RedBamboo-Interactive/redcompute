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
    ConfigUpdate = 1 << 6,
    ImageAttachments = 1 << 7,
    ProjectDiscovery = 1 << 8,
    Generate = 1 << 9,
}
