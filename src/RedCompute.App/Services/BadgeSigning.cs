using System.IO;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Auth.Vault;

namespace RedCompute.App.Services;

/// <summary>
/// RedCompute's one badge signing decision per process, made on first use with the same rules RedLeaf
/// applies: Leaf Vault signs and RedCompute holds only the public key, or (no vault installed) the
/// shared key exactly as before. It is shared because provider settings are synced from RedLeaf before
/// the server starts, and the server's auth and every outgoing client must agree on one mode.
/// </summary>
internal static class BadgeSigning
{
    internal static string SuiteDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");

    private static readonly Lazy<JwtOptions> LazyOptions = new(() =>
        BadgeSigningBootstrap.Resolve(SuiteDirectory, "redcompute",
            log: message => App.LogService.Log(RedBamboo.AppHost.Logging.LogLevel.Info, "auth", message)).ToJwtOptions());

    private static readonly Lazy<JwtService> LazyShared = new(() => new JwtService(Options));

    /// <summary>The options the server's auth registers, so the server and every client share one authority.</summary>
    internal static JwtOptions Options => LazyOptions.Value;

    /// <summary>The service that mints RedCompute's own outgoing badges.</summary>
    internal static JwtService Shared => LazyShared.Value;
}
