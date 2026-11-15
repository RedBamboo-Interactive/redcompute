using System.IO;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Storage;

namespace RedCompute.App.Services;

/// <summary>
/// Where RedBamboo.AppHost's <see cref="GoogleTokenStore"/> keeps RedCompute's Google API tokens.
/// Its own default is a lowercase <c>redsuite</c> folder: the same folder as <c>RedSuite</c> on
/// Windows, a different one on Linux. RedCompute names the canonical suite folder instead, and on
/// Linux keeps using the lowercase folder while only it holds tokens, so nobody loses a token.
/// </summary>
internal static class GoogleTokenLocation
{
    /// <summary>The token metadata file <see cref="GoogleTokenStore"/> writes in its directory.</summary>
    internal const string TokenFileName = "google-api-tokens.json";

    /// <summary>The folder AppHost defaulted to before RedCompute named one.</summary>
    internal const string LegacyFolderName = "redsuite";

    /// <param name="caseSensitiveFileSystem">True on Linux, where <c>redsuite</c> and <c>RedSuite</c> differ.</param>
    internal static string Resolve(ComputeStorage storage, bool caseSensitiveFileSystem, Func<string, bool> fileExists)
    {
        var canonical = storage.SuiteRoot;
        if (!caseSensitiveFileSystem || fileExists(Path.Combine(canonical, TokenFileName)))
            return canonical;

        var parent = Path.GetDirectoryName(canonical);
        if (string.IsNullOrEmpty(parent)) return canonical;
        var legacy = Path.Combine(parent, LegacyFolderName);
        return fileExists(Path.Combine(legacy, TokenFileName)) ? legacy : canonical;
    }

    /// <summary>The same options with <see cref="GoogleAuthOptions.DataDirectory"/> set, unless one is already set.</summary>
    internal static GoogleAuthOptions? WithDirectory(GoogleAuthOptions? options, string directory)
        => options is null || options.DataDirectory is not null
            ? options
            : new GoogleAuthOptions
            {
                ClientId = options.ClientId,
                ClientSecret = options.ClientSecret,
                ExtraScopes = options.ExtraScopes,
                DataDirectory = directory,
            };
}
