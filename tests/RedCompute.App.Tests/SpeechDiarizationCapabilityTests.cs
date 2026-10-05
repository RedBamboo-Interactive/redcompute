using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Plugin.DiarizationLocal;
using RedCompute.Plugin.LocalWsl;
using RedCompute.Plugin.SttLocal;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SpeechDiarizationCapabilityTests
{
    [Fact]
    public void Default_config_exposes_a_pinned_eight_speaker_diarization_provider()
    {
        var config = ConfigManager.CreateDefault();
        var capability = config.Capabilities["diarization"];
        var provider = capability.Providers["local-wsl"];

        Assert.Equal("local-wsl", capability.ActiveProvider);
        Assert.Equal("DiarizationLocal", provider.Type);
        Assert.Equal("nvidia/Nemotron-3-Diarization", provider.Model);
        Assert.Equal("f667ed73aee57d40cc39428eb768b4fd87a0a29e", provider.ModelRevision);
        Assert.Equal(8768, provider.BackendPort);
        Assert.EndsWith(Path.Combine("backends", "diarization-local"), provider.ServerPath);
    }

    [Fact]
    public void Provider_discovery_finds_the_diarization_plugin()
    {
        _ = typeof(DiarizationLocalProvider);
        var discovery = new ProviderDiscovery(_ => { });

        discovery.ScanAssemblies();

        Assert.Contains("DiarizationLocal", discovery.AvailableTypes);
        var provider = discovery.Create(
            "DiarizationLocal",
            ConfigManager.CreateDefault().Capabilities["diarization"].Providers["local-wsl"],
            "diarization",
            _ => { });
        Assert.NotNull(provider);
        Assert.Equal("diarization", provider!.CapabilitySlug);
    }

    [Fact]
    public void Existing_stt_contract_keeps_diarization_optional()
    {
        var provider = new SttLocalProvider(new ProviderConfig
        {
            Type = "SttLocal",
            BackendPort = 8766,
        }, "stt", _ => { });

        Assert.False(provider.InputParameters["diarize"].Required);
        Assert.Equal(false, provider.InputParameters["diarize"].Default);
        Assert.True(provider.InputParameters["word_timestamps"].Required is false);
        Assert.Empty(provider.ValidateParameters(new Dictionary<string, object?>
        {
            ["audio_base64"] = "AA==",
        }));
    }

    [Fact]
    public void Streaming_stt_diarization_requires_stable_session_and_chunk_ids()
    {
        var provider = new SttLocalProvider(new ProviderConfig
        {
            Type = "SttLocal",
            BackendPort = 8766,
        }, "stt", _ => { });

        var errors = provider.ValidateParameters(new Dictionary<string, object?>
        {
            ["diarize"] = true,
            ["diarization_mode"] = "streaming",
        });

        Assert.Equal("required when streaming diarization is enabled", errors["diarization_session_id"]);
        Assert.Equal("required when streaming diarization is enabled", errors["diarization_chunk_id"]);
    }

    [Fact]
    public void Standalone_contract_rejects_invalid_stream_and_speaker_bounds()
    {
        var provider = new DiarizationLocalProvider(new ProviderConfig
        {
            Type = "DiarizationLocal",
            BackendPort = 8768,
        }, "diarization", _ => { });

        var errors = provider.ValidateParameters(new Dictionary<string, object?>
        {
            ["mode"] = "streaming",
            ["max_speakers"] = 9,
        });

        Assert.Equal("required for streaming mode", errors["session_id"]);
        Assert.Equal("required for streaming mode", errors["chunk_id"]);
        Assert.Equal("must be an integer from 1 through 8", errors["max_speakers"]);
    }

    [Fact]
    public void Standalone_contract_describes_anonymous_identity_without_claiming_names()
    {
        var provider = new DiarizationLocalProvider(new ProviderConfig
        {
            Type = "DiarizationLocal",
            BackendPort = 8768,
        }, "diarization", _ => { });

        Assert.Equal("diarization.v1", provider.ContractVersion);
        Assert.Equal("/diarize", provider.ProxyGeneratePath);
        Assert.Equal("application/json", provider.OutputSchema.ContentType);
        Assert.Equal(8, provider.InputParameters["max_speakers"].Max);
        Assert.DoesNotContain(provider.InputParameters.Keys,
            key => key.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Local_wsl_launch_is_self_bootstrapping_and_passes_the_pinned_revision()
    {
        var provider = new LocalWslProvider(new ProviderConfig
        {
            Type = "DiarizationLocal",
            WslDistro = "Ubuntu-24.04",
            VenvPath = "~/diarization-env",
            ServerPath = @"T:\Projects\redcompute\backends\diarization-local",
            BackendPort = 8768,
            Model = "nvidia/Nemotron-3-Diarization",
            ModelRevision = "f667ed73aee57d40cc39428eb768b4fd87a0a29e",
        }, "diarization", _ => { });

        var start = provider.BuildStartInfo();
        var wrapper = start.ArgumentList[^1];
        var command = provider.BuildWslBackendCommand();

        Assert.Equal("wsl.exe", start.FileName);
        Assert.Contains("if [ -f ~/diarization-env/bin/activate ]", command);
        Assert.Contains("--revision f667ed73aee57d40cc39428eb768b4fd87a0a29e", command);
        Assert.Contains("REDCOMPUTE_LOCALWSL_PROCESS=", wrapper);
        Assert.DoesNotContain("pkill", wrapper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_wsl_stop_targets_only_the_verified_process_group()
    {
        var script = LocalWslProvider.BuildWslStopScript(4123, "987654");

        Assert.Contains("/proc/$pid/stat", script);
        Assert.Contains("$current\" = \"$expected", script);
        Assert.Contains("kill -TERM -- \"-$pid\"", script);
        Assert.DoesNotContain("pkill", script, StringComparison.OrdinalIgnoreCase);
    }
}
