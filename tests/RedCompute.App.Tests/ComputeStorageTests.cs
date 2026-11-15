using System.Reflection;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Sessions;
using RedCompute.Core.Storage;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Data relocation phase 1B: every persistent location RedCompute decides comes from
/// <see cref="ComputeStorage"/>. Without <c>REDCOMPUTE_DATA_ROOT</c> each one is exactly today's path;
/// with it, each one is under the variable's folder, and RedSuite stays where it is.
/// </summary>
public sealed class ComputeStorageTests
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Every named location, with its path relative to the data root ('/'-separated).</summary>
    private static IEnumerable<(string Name, string Actual, string Relative)> Locations(ComputeStorage storage) =>
    [
        ("data root", storage.DataRoot, ""),
        ("redcompute.db", storage.Database, "redcompute.db"),
        ("outputs", storage.Outputs, "outputs"),
        ("input attachments", storage.InputAttachments, "input-attachments"),
        ("attachments.db", storage.AttachmentsDatabase, "input-attachments/attachments.db"),
        ("conversation sessions", storage.ConversationSessions, "conversation-sessions"),
        ("config.json", storage.ConfigFile, "config.json"),
        ("entity-cache.json", storage.EntityCache, "entity-cache.json"),
        ("quality-mode-cache.json", storage.QualityModeCache, "quality-mode-cache.json"),
        ("node.json folder", storage.NodeIdentityDirectory, ""),
        ("long-lived processes folder", storage.LongLivedProcessesDirectory, ""),
        ("session-processes", storage.SessionProcesses, "session-processes"),
        ("planned-restart.json", storage.PlannedRestartCheckpoint, "planned-restart.json"),
        ("capability overrides", storage.CapabilityOverrides, "capabilities"),
        (".defender-exclusions", storage.DefenderExclusionsMarker, ".defender-exclusions"),
        ("telemetry.db", storage.TelemetryDatabase, "telemetry.db"),
        ("Logs", storage.Logs, "Logs"),
        ("claude plugin", storage.PluginDirectory("claude-code"), "plugins/claude-code"),
        ("codex plugin", storage.PluginDirectory("codex"), "plugins/codex"),
        ("opencode plugin", storage.PluginDirectory("opencode"), "plugins/opencode"),
    ];

    private static ComputeStorage Resolve(string? dataRoot)
        => ComputeStorage.FromEnvironment(
            name => name == ComputeStorage.DataRootVariable ? dataRoot : null, Environment.GetFolderPath);

    /// <summary>The literal path today's code built, joined with the OS separator by hand.</summary>
    private static string Literal(string root, string relative, char separator)
        => relative.Length == 0 ? root : root + separator + relative.Replace('/', separator);

    [WindowsFact("the Windows form of today's paths")]
    public void Without_the_variable_every_location_is_todays_Windows_path()
    {
        var storage = Resolve(null);

        Assert.False(storage.IsRelocated);
        foreach (var (name, actual, relative) in Locations(storage))
            Assert.True(Literal(LocalAppData + @"\RedCompute", relative, '\\') == actual, $"{name}: {actual}");
        Assert.Equal(LocalAppData + @"\RedSuite", storage.SuiteRoot);
    }

    [NonWindowsFact("the Linux form of today's paths")]
    public void Without_the_variable_every_location_is_todays_Linux_path()
    {
        // What LocalApplicationData answers on Linux: $XDG_DATA_HOME when absolute, else ~/.local/share.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var expectedBase = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Environment.GetEnvironmentVariable("HOME") + "/.local/share";
        Assert.Equal(expectedBase, LocalAppData);

        var storage = Resolve(null);

        Assert.False(storage.IsRelocated);
        foreach (var (name, actual, relative) in Locations(storage))
            Assert.True(Literal(expectedBase + "/RedCompute", relative, '/') == actual, $"{name}: {actual}");
        Assert.Equal(expectedBase + "/RedSuite", storage.SuiteRoot);
    }

    [Fact]
    public void With_the_variable_every_location_is_under_it_and_RedSuite_does_not_move()
    {
        var root = Path.Combine(Path.GetTempPath(), "redcompute-data-root-" + Guid.NewGuid().ToString("N"));
        var storage = Resolve(root);

        Assert.True(storage.IsRelocated);
        Assert.Equal(root, storage.DataRoot);
        foreach (var (name, actual, relative) in Locations(storage))
        {
            Assert.True(Literal(root, relative, Path.DirectorySeparatorChar) == actual, $"{name}: {actual}");
            Assert.False(actual.StartsWith(Path.Combine(LocalAppData, "RedCompute"), StringComparison.OrdinalIgnoreCase), $"{name} stayed at the default root: {actual}");
        }
        Assert.Equal(Resolve(null).SuiteRoot, storage.SuiteRoot);
        Assert.False(Directory.Exists(root)); // Resolving never creates anything.
    }

    [Fact]
    public void A_relocated_root_is_normalised()
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-norm", "..", "rc-data");
        Assert.Equal(Path.GetFullPath(root), Resolve(root).DataRoot);
    }

    public static TheoryData<string> InvalidValues()
    {
        var values = new TheoryData<string> { "", "   ", "data", Path.Combine("relative", "data"), "./data", "~/data" };
        if (OperatingSystem.IsWindows())
        {
            values.Add(@"C:data");    // drive-relative
            values.Add(@"\data");     // rooted on the current drive, not fully qualified
        }
        return values;
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void A_value_that_is_not_an_absolute_path_is_a_configuration_error(string value)
    {
        var error = Assert.Throws<ComputeStorageConfigurationException>(() => Resolve(value));
        Assert.Contains(ComputeStorage.DataRootVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains("absolute path", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    public void A_plugin_id_cannot_leave_the_plugins_folder(string pluginId)
        => Assert.Throws<ArgumentException>(() => Resolve(null).PluginDirectory(pluginId));

    /// <summary>
    /// The sites themselves resolve through this process's storage (whatever the environment says),
    /// so with the two tests above the chain from each site to today's path, or to the variable, holds.
    /// Sites with a settable test hook (the plugin <c>DbPath</c>s, the session ledger) are left out:
    /// other tests change them concurrently; the source guard covers them.
    /// </summary>
    [Fact]
    public void The_sites_resolve_through_this_processes_storage()
    {
        var storage = ComputeStorage.Current;
        var configured = Environment.GetEnvironmentVariable(ComputeStorage.DataRootVariable);
        if (configured is null)
            Assert.Equal(Resolve(null).DataRoot, storage.DataRoot);
        else
            Assert.Equal(Path.GetFullPath(configured), storage.DataRoot);

        Assert.Equal(storage.ConfigFile, PrivateStatic<string>(typeof(ConfigManager), "ConfigPath"));
        Assert.Equal(storage.EntityCache, PrivateStatic<string>(typeof(ProviderConfigService), "CachePath"));
        Assert.Equal(storage.QualityModeCache, PrivateStatic<string>(typeof(QualityModeService), "DefaultCachePath"));
        Assert.Equal(storage.CapabilityOverrides, PrivateStatic<string>(typeof(CapabilityManifestLoader), "UserCapabilitiesDir"));
        Assert.Equal(storage.DefenderExclusionsMarker, PrivateStatic<string>(typeof(DefenderExclusionService), "MarkerPath"));
        Assert.Equal(storage.Outputs, JobApiResponse.OutputDirectory);
        Assert.Equal(storage.Outputs, PrivateStatic<string>(typeof(GenericCapabilityEndpoints), "OutputDir"));
        Assert.Equal(storage.PlannedRestartCheckpoint, PlannedRestartCheckpoint.DefaultPath);
        Assert.Equal(storage.NodeIdentityDirectory, NodeIdentityStore.DefaultDirectory);
        Assert.Equal(storage.LongLivedProcessesDirectory, LongLivedProcessRegistry.DefaultDirectory(LocalExecutionHost.Shared));
        Assert.Equal(storage.DataRoot, LocalExecutionHost.Shared.Platform.ComputeDataRoot);
        Assert.Equal(storage.SuiteRoot, BadgeSigning.SuiteDirectory);
    }

    private static T PrivateStatic<T>(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{type.Name}.{name} not found");
        return (T)field.GetValue(null)!;
    }

    // ---- Google API tokens: canonical RedSuite, lowercase fallback on Linux --------------------------

    private static readonly ComputeStorage Fixed = new(
        Path.Combine(Path.GetTempPath(), "rc", "RedCompute"), Path.Combine(Path.GetTempPath(), "rc", "RedSuite"), false);

    private static string Legacy => Path.Combine(Path.GetTempPath(), "rc", "redsuite");

    private static Func<string, bool> Present(params string[] directories)
        => path => directories.Any(directory => path == Path.Combine(directory, GoogleTokenLocation.TokenFileName));

    [Fact]
    public void Google_tokens_use_RedSuite_where_case_does_not_matter()
        => Assert.Equal(Fixed.SuiteRoot, GoogleTokenLocation.Resolve(Fixed, caseSensitiveFileSystem: false, Present(Legacy)));

    [Fact]
    public void Google_tokens_on_Linux_use_RedSuite_when_it_has_a_token()
        => Assert.Equal(Fixed.SuiteRoot,
            GoogleTokenLocation.Resolve(Fixed, caseSensitiveFileSystem: true, Present(Fixed.SuiteRoot, Legacy)));

    [Fact]
    public void Google_tokens_on_Linux_fall_back_to_lowercase_redsuite_when_only_it_has_a_token()
        => Assert.Equal(Legacy, GoogleTokenLocation.Resolve(Fixed, caseSensitiveFileSystem: true, Present(Legacy)));

    [Fact]
    public void Google_tokens_on_Linux_start_in_RedSuite_when_nobody_has_one()
        => Assert.Equal(Fixed.SuiteRoot, GoogleTokenLocation.Resolve(Fixed, caseSensitiveFileSystem: true, Present()));

    [Fact]
    public void Google_options_gain_the_directory_and_keep_everything_else()
    {
        var options = new GoogleAuthOptions { ClientId = "id", ClientSecret = "secret", ExtraScopes = ["scope"] };
        var placed = GoogleTokenLocation.WithDirectory(options, "/suite")!;

        Assert.Equal(("id", "secret", "/suite"), (placed.ClientId, placed.ClientSecret, placed.DataDirectory));
        Assert.Equal(["scope"], placed.ExtraScopes);
        Assert.Null(GoogleTokenLocation.WithDirectory(null, "/suite"));
        var explicitDirectory = new GoogleAuthOptions { ClientId = "id", ClientSecret = "s", DataDirectory = "/mine" };
        Assert.Same(explicitDirectory, GoogleTokenLocation.WithDirectory(explicitDirectory, "/suite"));
    }
}
