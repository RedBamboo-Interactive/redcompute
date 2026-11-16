namespace RedCompute.PluginSdk;

/// <summary>
/// Which provider features the host platform supports. Local Python/GPU backends (TTS, STT,
/// diarization, ComfyUI, Kev) are launched through wsl.exe or cmd.exe, so they are Windows-only
/// for now; elsewhere a provider reports <see cref="Core.Providers.BackendStatus.Unsupported"/>
/// with <see cref="LocalBackendUnsupported"/> instead of trying to spawn them.
/// </summary>
public static class PlatformSupport
{
    /// <summary>Stable error code for API responses about a platform-unsupported provider.</summary>
    public const string UnsupportedCode = "platform_not_supported";

    public static bool CanLaunchLocalBackends => OperatingSystem.IsWindows();

    public static string PlatformName =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsMacOS() ? "macOS"
        : "this platform";

    public static string LocalBackendUnsupported(string provider) =>
        $"{provider} is not supported on {PlatformName} yet: local backends are launched through WSL or cmd.exe, which only exist on Windows";
}
