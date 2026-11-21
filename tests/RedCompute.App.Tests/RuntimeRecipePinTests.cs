using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.Plugin.ComfyUI;
using RedCompute.Plugin.LocalWsl;
using RedCompute.PluginSdk.Hosting;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The pin for the recipe move (Phase 4 P3, design 2.4): the spawn spec ComfyUI and LocalWsl build for
/// each node-zero config shape, captured before the spawn building moved into
/// <c>RuntimeRecipes</c> (tests/Goldens/Recipes/node-zero-specs.json, recorded at the commit before P3).
/// The launch goldens pin two shapes per kind end to end; this pins many more at the spec, field
/// for field. Record again only on purpose: <c>REDCOMPUTE_RECORD_RECIPES=1</c>.
/// </summary>
public sealed class RuntimeRecipePinTests : IDisposable
{
    private const string Root = @"C:\Golden Root";
    private readonly string _portable = Path.Combine(Path.GetTempPath(), "redcompute-recipe-pin", Guid.NewGuid().ToString("N"));

    private static string GoldenPath => Path.Combine(LaunchGoldens.Directory, "..", "Recipes", "node-zero-specs.json");

    public RuntimeRecipePinTests()
    {
        // A portable ComfyUI: python_embeded beside the server folder is what the native recipe picks.
        Directory.CreateDirectory(Path.Combine(_portable, "ComfyUI"));
        Directory.CreateDirectory(Path.Combine(_portable, "python_embeded"));
        File.WriteAllText(Path.Combine(_portable, "python_embeded", "python.exe"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_portable, recursive: true); } catch { }
    }

    /// <summary>name → config: every key the ComfyUI recipe reads, alone and together, native and WSL.</summary>
    private IEnumerable<(string Name, ProviderConfig Config)> ComfyShapes()
    {
        ProviderConfig Comfy(string? distro, string? server = Root + @"\ComfyUI", string? venv = null, int? port = null,
            (string Key, object? Value)[]? extra = null) => new()
        {
            Type = "ComfyUI",
            WslDistro = distro,
            ServerPath = server,
            VenvPath = venv,
            BackendPort = port,
            Extra = extra is null ? null : extra.ToDictionary(item => item.Key, item => item.Value),
        };

        foreach (var (mode, distro) in new[] { ("native", (string?)null), ("wsl", "Ubuntu-24.04") })
        {
            yield return ($"comfyui-{mode}-minimal", Comfy(distro));
            yield return ($"comfyui-{mode}-no-server-path", Comfy(distro, server: null));
            yield return ($"comfyui-{mode}-venv", Comfy(distro, venv: distro is null ? Root + @"\venv" : "/home/me/venv"));
            yield return ($"comfyui-{mode}-venv-tilde-space", Comfy(distro, venv: "~/comfy env"));
            yield return ($"comfyui-{mode}-port-host", Comfy(distro, port: 18188, extra: [("Host", "0.0.0.0")]));
            yield return ($"comfyui-{mode}-server-args", Comfy(distro, extra: [("ServerArgs", "  --preview-method auto --lowvram ")]));
            yield return ($"comfyui-{mode}-server-args-blank", Comfy(distro, extra: [("ServerArgs", "   ")]));
            yield return ($"comfyui-{mode}-directories", Comfy(distro, extra:
            [
                ("ModelsDirectory", Root + @"\models"), ("OutputDirectory", Root + @"\out put"),
                ("InputDirectory", Root + @"\input"), ("TempDirectory", Root + @"\temp"), ("UserDirectory", Root + @"\user"),
            ]));
            yield return ($"comfyui-{mode}-directories-tilde", Comfy(distro, extra:
                [("ModelsDirectory", "~/models"), ("OutputDirectory", "~"), ("UserDirectory", "/home/me/it's user")]));
            yield return ($"comfyui-{mode}-user-directory-derives-database", Comfy(distro, extra: [("UserDirectory", Root + @"\user")]));
            yield return ($"comfyui-{mode}-user-directory-unix", Comfy(distro, extra: [("UserDirectory", "/srv/comfy/user")]));
            yield return ($"comfyui-{mode}-database-sqlite-windows", Comfy(distro, extra: [("DatabaseUrl", @"sqlite:///D:\db\comfy.db")]));
            yield return ($"comfyui-{mode}-database-sqlite-tilde", Comfy(distro, extra: [("DatabaseUrl", "sqlite:///~/comfy.db")]));
            yield return ($"comfyui-{mode}-database-other", Comfy(distro, extra:
                [("DatabaseUrl", " postgresql://u@h/db "), ("UserDirectory", Root + @"\user")]));
            yield return ($"comfyui-{mode}-blank-extras", Comfy(distro, extra:
                [("ModelsDirectory", "  "), ("DatabaseUrl", ""), ("ServerArgs", null)]));
            yield return ($"comfyui-{mode}-quotes", Comfy(distro, server: Root + @"\it's ""Comfy""", extra: [("ModelsDirectory", Root + @"\a $b `c`")]));
        }
        yield return ("comfyui-native-portable-python", Comfy(null, server: Path.Combine(_portable, "ComfyUI")));
        yield return ("comfyui-native-venv-beats-portable", Comfy(null, server: Path.Combine(_portable, "ComfyUI"), venv: Root + @"\venv"));
    }

    /// <summary>name → config: every key the LocalWsl recipe reads, native and WSL.</summary>
    private static IEnumerable<(string Name, ProviderConfig Config)> LocalWslShapes()
    {
        ProviderConfig Wsl(string? distro, string? server, string? venv = null, int? port = 18998, string? model = null,
            string? revision = null, string type = "LocalWsl") => new()
        {
            Type = type, WslDistro = distro, ServerPath = server, VenvPath = venv, BackendPort = port,
            Model = model, ModelRevision = revision,
        };

        yield return ("localwsl-native-minimal", Wsl(null, Root + @"\server"));
        yield return ("localwsl-native-no-port", Wsl(null, Root + @"\server", port: null));
        yield return ("localwsl-native-model-revision", Wsl(null, Root + @"\server", model: "Qwen/Qwen3-TTS", revision: "abc123"));
        yield return ("localwsl-native-type-localnative", Wsl(null, Root + @"\server", type: "LocalNative"));
        yield return ("localwsl-native-no-server-path", Wsl(null, null));
        yield return ("localwsl-wsl-minimal", Wsl("Ubuntu-24.04", "/home/me/server"));
        yield return ("localwsl-wsl-venv", Wsl("Ubuntu-24.04", "/home/me/server", venv: "/home/me/venv"));
        yield return ("localwsl-wsl-windows-server-path", Wsl("Ubuntu-24.04", @"D:\speech\server"));
        yield return ("localwsl-wsl-no-server-path", Wsl("Ubuntu-24.04", null));
        yield return ("localwsl-wsl-quote-in-path", Wsl("Ubuntu-24.04", "/home/me/it's server", model: "m", revision: "r"));
        yield return ("localwsl-wsl-port-zero", Wsl("Ubuntu-24.04", "/home/me/server", port: 0));
        yield return ("localwsl-wsl-stt", Wsl("Ubuntu-24.04", "/home/me/stt", venv: "/home/me/stt/.venv", model: "large-v3", type: "SttLocal"));
    }

    private JsonObject Observe()
    {
        var specs = new JsonObject();
        foreach (var (name, config) in ComfyShapes())
            specs[name] = Describe(new ComfyUIProvider(config, "image-gen", _ => { }).BuildSpawnSpec());
        foreach (var (name, config) in LocalWslShapes())
            specs[name] = Describe(new LocalWslProvider(config, "tts", _ => { }).BuildSpawnSpec());
        return specs;
    }

    /// <summary>Every field of the spec, as written; the portable folder is the one thing that varies.</summary>
    private JsonNode Describe(HostSpawnSpec spec)
    {
        var node = JsonSerializer.SerializeToNode(spec, new JsonSerializerOptions
        {
            Converters = { new EncodingName(), new System.Text.Json.Serialization.JsonStringEnumConverter() },
        })!;
        return JsonNode.Parse(node.ToJsonString().Replace(JsonEncodedText.Encode(_portable).ToString(), "<portable>"))!;
    }

    /// <summary>An encoding by its web name (null stays null).</summary>
    private sealed class EncodingName : System.Text.Json.Serialization.JsonConverter<System.Text.Encoding>
    {
        public override System.Text.Encoding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, System.Text.Encoding value, JsonSerializerOptions options)
            => writer.WriteStringValue($"{value.WebName}/{value.GetPreamble().Length}");
    }

    [WindowsFact("the recipes build Windows paths (python.exe, cmd.exe, wsl.exe), as node zero runs them")]
    public void Every_node_zero_config_shape_builds_the_spec_it_built_before_the_move()
    {
        var observed = Observe();
        if (Environment.GetEnvironmentVariable("REDCOMPUTE_RECORD_RECIPES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllText(GoldenPath, observed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return;
        }
        var golden = JsonNode.Parse(File.ReadAllText(GoldenPath))!.AsObject();
        Assert.Equal(golden.Select(entry => entry.Key).Order(StringComparer.Ordinal),
            observed.Select(entry => entry.Key).Order(StringComparer.Ordinal));
        foreach (var (name, expected) in golden)
            Assert.True(JsonNode.DeepEquals(expected, observed[name]),
                $"{name}:\n  golden   {expected!.ToJsonString()}\n  observed {observed[name]!.ToJsonString()}");
        Assert.True(golden.Count >= 40, $"only {golden.Count} shapes");
    }
}
