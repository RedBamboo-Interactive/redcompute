using RedCompute.Core.Configuration;
using RedCompute.Plugin.TtsLocal;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Characterization (node zero, C7): TTS Local lists a custom voice for every directory under the
/// voices base that holds a model checkpoint, after the built-in voices, in directory order.
/// </summary>
public sealed class TtsLocalVoicesTests : IDisposable
{
    private readonly string _voices = Path.Combine(Path.GetTempPath(), "redcompute-tts-voices", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Custom_voices_are_the_directories_with_a_checkpoint()
    {
        foreach (var name in new[] { "Bravo", "Alpha", "Charlie" })
            Directory.CreateDirectory(Path.Combine(_voices, name, "model", "checkpoint"));
        File.WriteAllText(Path.Combine(_voices, "Alpha", "model", "checkpoint", "model.safetensors"), "");
        File.WriteAllText(Path.Combine(_voices, "Charlie", "model", "checkpoint", "model.safetensors"), "");
        File.WriteAllText(Path.Combine(_voices, "loose-file.txt"), "");

        var voices = Provider(_voices).InputParameters["voice"].Enum;

        Assert.Equal(["Serena", "Aiden", "Ryan", "Vivian", "Alpha", "Charlie"], voices);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("does-not-exist")]
    public void Without_a_voices_directory_only_the_builtins_are_listed(string? basePath)
    {
        var path = basePath == "does-not-exist" ? Path.Combine(_voices, basePath) : basePath;
        Assert.Equal(["Serena", "Aiden", "Ryan", "Vivian"], Provider(path).InputParameters["voice"].Enum);
    }

    private static TtsLocalProvider Provider(string? voicesBasePath) => new(new ProviderConfig
    {
        Type = "TtsLocal",
        ServerPath = "/srv/tts",
        WslDistro = "Golden-Distro",
        BackendPort = 18997,
        VoicesBasePath = voicesBasePath,
    }, "tts", _ => { });

    public void Dispose()
    {
        try { Directory.Delete(_voices, recursive: true); } catch { }
    }
}
