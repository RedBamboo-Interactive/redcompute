namespace RedCompute.Core.Providers;

public enum BackendStatus
{
    Stopped,
    Starting,
    Running,
    Error,
    Draining,
    /// <summary>
    /// The provider cannot run on this host platform (for example a WSL-launched GPU backend on
    /// Linux). Starting it again will not help; <see cref="IBackendProvider.StatusDetail"/> says why.
    /// </summary>
    Unsupported
}
