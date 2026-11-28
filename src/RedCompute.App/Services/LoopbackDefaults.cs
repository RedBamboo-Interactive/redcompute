using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;

namespace RedCompute.App.Services;

/// <summary>
/// The addresses RedCompute uses for a Leaf service on this machine when nothing configures one. The suite
/// listens on IPv4 only, and on Windows <c>localhost</c> resolves to <c>::1</c> first: every fresh connection to
/// it waits about two seconds for that refusal before it tries 127.0.0.1. So the defaults name 127.0.0.1. A
/// configured endpoint, a remote node's address or a container's is used exactly as configured.
/// </summary>
internal static class LoopbackDefaults
{
    /// <summary>The base address of a client whose every request names its own absolute URL.</summary>
    internal const string BaseAddress = "http://127.0.0.1";

    /// <summary>The kernel's default address: what AppHost's own default (<c>http://localhost:18804</c>) means.</summary>
    internal const string KernelBaseUrl = "http://127.0.0.1:18804";

    /// <summary>
    /// AppHost's auth registers its kernel reads (users, refresh tokens, roles) with
    /// <see cref="PermissionDataSourceOptions"/> at its <c>localhost</c> default. RedCompute never configures
    /// it, so it registers the same default by its IPv4 address after AppHost's: the last registration is the
    /// one resolved. One that already names another address is left alone.
    /// </summary>
    internal static void ReplaceKernelDefault(IServiceCollection services)
    {
        var registered = services.LastOrDefault(d => d.ServiceType == typeof(PermissionDataSourceOptions))
            ?.ImplementationInstance as PermissionDataSourceOptions;
        if (registered is not null && !IsLocalhostDefault(registered.RedLeafBaseUrl)) return;
        services.AddSingleton(new PermissionDataSourceOptions { RedLeafBaseUrl = KernelBaseUrl });
    }

    private static bool IsLocalhostDefault(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 18804;
}
