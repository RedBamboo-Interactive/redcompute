namespace RedCompute.Node;

/// <summary>A verb failed for a reason the person can act on: <see cref="Exception.Message"/> is what the CLI prints.</summary>
public sealed class NodeCommandException(string code, string message, int exitCode = NodeExitCodes.Failed, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}

public static class NodeExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    /// <summary>Another agent already runs for this user.</summary>
    public const int AlreadyRunning = 3;
    /// <summary>The agent is not running (a verb that needs it).</summary>
    public const int NotRunning = 4;
    /// <summary>Refused: running as root on Linux.</summary>
    public const int Refused = 77;
    /// <summary>A configuration error (EX_CONFIG), as RedCompute's data root.</summary>
    public const int Configuration = 78;
}
