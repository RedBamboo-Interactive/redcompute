using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class DiscoverEndpoints
{
    public static void Map(WebApplication app, RedComputeConfig config, CapabilityRegistry registry)
    {
        app.MapGet("/discover", async () =>
        {
            var capabilities = new List<CapabilityManifest>();

            foreach (var (slug, entry) in registry.Capabilities)
            {
                var status = entry.ActiveProvider != null
                    ? await entry.ActiveProvider.GetStatusAsync()
                    : BackendStatus.Stopped;

                var endpoints = GetEndpointsForCapability(slug, registry);

                capabilities.Add(new CapabilityManifest
                {
                    Slug = slug,
                    Type = entry.Definition.Type.ToString(),
                    DisplayName = entry.Definition.DisplayName,
                    Status = status.ToString(),
                    Provider = entry.ActiveProvider?.Name,
                    Sleeping = entry.IsSleeping,
                    Endpoints = endpoints
                });
            }

            var manifest = new ServiceManifest
            {
                Version = "0.1.0",
                ApiBase = $"http://localhost:{config.ApiPort}",
                Capabilities = capabilities
            };

            return Results.Ok(manifest);
        });
    }

    private static List<EndpointManifest> GetEndpointsForCapability(string slug, CapabilityRegistry registry)
    {
        return slug switch
        {
            "tts" => new List<EndpointManifest>
            {
                new()
                {
                    Method = "POST",
                    Path = "/tts/generate",
                    Description = "Generate speech audio from text. Returns audio/wav (default) or streams audio/pcm chunks when stream=true.",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["text"] = new() { Type = "string", Required = true, Description = "Text to synthesize" },
                        ["voice"] = new() { Type = "string", Required = false, Default = "Serena", Enum = TtsVoiceDiscovery.AllVoices(registry), Description = "Speaker voice name" },
                        ["language"] = new() { Type = "string", Required = false, Default = "English", Enum = ["English", "Chinese", "French", "Japanese", "Korean"], Description = "Language for synthesis" },
                        ["emotion"] = new() { Type = "string", Required = false, Default = "neutral", Enum = ["neutral", "excited", "happy", "sad", "angry", "sarcastic", "curious", "confident"], Description = "Emotional tone" },
                        ["instruct"] = new() { Type = "string", Required = false, Description = "Natural language instruction for voice style (overrides emotion). E.g. 'Speak with warm enthusiasm'" },
                        ["speed"] = new() { Type = "number", Required = false, Default = 1.0, Min = 0.5, Max = 2.0, Description = "Playback speed multiplier" },
                        ["stream"] = new() { Type = "boolean", Required = false, Default = false, Description = "If true, streams raw PCM audio (s16le, 24kHz, mono) as chunks arrive. Response headers include X-Audio-Sample-Rate, X-Audio-Channels, X-Audio-Format." }
                    },
                    Returns = new ReturnSchema { ContentType = "audio/wav", Streaming = false, MediaCategory = "audio", OutputEndpoint = "/tts/jobs/{id}/output" }
                },
                new()
                {
                    Method = "GET",
                    Path = "/tts/jobs/{id}/output",
                    Description = "Download the generated audio for a completed TTS job",
                    Returns = new ReturnSchema { ContentType = "audio/wav", Streaming = false, MediaCategory = "audio" }
                },
                new()
                {
                    Method = "GET",
                    Path = "/tts/voices",
                    Description = "List available speaker voices with their supported languages",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                }
            },
            "stt" => new List<EndpointManifest>
            {
                new()
                {
                    Method = "POST",
                    Path = "/stt/transcribe",
                    Description = "Transcribe audio to text",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["audio"] = new() { Type = "file", Required = true, Description = "Audio file (wav, mp3, opus, flac)" },
                        ["language"] = new() { Type = "string", Required = false, Default = "auto", Description = "Language hint (ISO 639-1 code or 'auto')" }
                    },
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                }
            },
            "image-gen" => new List<EndpointManifest>
            {
                new()
                {
                    Method = "POST",
                    Path = "/image-gen/generate",
                    Description = "Generate image/video from text prompt using ComfyUI workflows. Returns image bytes (sync) or job ID (async with ?async=true).",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["prompt"] = new() { Type = "string", Required = true, Description = "Text description of the image to generate" },
                        ["workflow"] = new() { Type = "string", Required = false, Default = "z_turbo", Description = "Workflow name. GET /image-gen/workflows for available options" },
                        ["negative"] = new() { Type = "string", Required = false, Default = "", Description = "Negative prompt (things to avoid)" },
                        ["seed"] = new() { Type = "integer", Required = false, Description = "Random seed for reproducibility. Omit for random." },
                        ["width"] = new() { Type = "integer", Required = false, Description = "Image width (workflow-dependent, only if workflow supports it)" },
                        ["height"] = new() { Type = "integer", Required = false, Description = "Image height (workflow-dependent, only if workflow supports it)" },
                        ["image_url"] = new() { Type = "string", Required = false, Description = "Source image URL for img2img or video workflows" }
                    },
                    Returns = new ReturnSchema { ContentType = "image/png", Streaming = false, MediaCategory = "image", OutputEndpoint = "/image-gen/jobs/{id}/output" }
                },
                new()
                {
                    Method = "GET",
                    Path = "/image-gen/workflows",
                    Description = "List available ComfyUI workflows with their parameters",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                },
                new()
                {
                    Method = "GET",
                    Path = "/image-gen/workflows/{name}",
                    Description = "Get details of a specific workflow including node mapping",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                },
                new()
                {
                    Method = "GET",
                    Path = "/image-gen/jobs/{id}/progress",
                    Description = "Get real-time progress of an image generation job (0.0 to 1.0)",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                },
                new()
                {
                    Method = "GET",
                    Path = "/image-gen/jobs/{id}/output",
                    Description = "Download the generated image/video for a completed job",
                    Returns = new ReturnSchema { ContentType = "image/png", Streaming = false, MediaCategory = "image" }
                }
            },
            "music-gen" => new List<EndpointManifest>
            {
                new()
                {
                    Method = "POST",
                    Path = "/music-gen/generate",
                    Description = "Generate music via Suno AI. Returns MP3 audio (sync) or job ID (async with ?async=true). Suno produces 2 variations per generation.",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["prompt"] = new() { Type = "string", Required = true, Description = "Musical description — what the song should sound like, mood, instruments, lyrics" },
                        ["style"] = new() { Type = "string", Required = false, Default = "", Description = "Genre/style tags (e.g. 'cinematic orchestral epic')" },
                        ["title"] = new() { Type = "string", Required = false, Default = "", Description = "Track title" },
                        ["instrumental"] = new() { Type = "boolean", Required = false, Default = true, Description = "If true, generate instrumental only (no vocals)" }
                    },
                    Returns = new ReturnSchema { ContentType = "audio/mpeg", Streaming = false, MediaCategory = "audio", OutputEndpoint = "/music-gen/jobs/{id}/output" }
                },
                new()
                {
                    Method = "GET",
                    Path = "/music-gen/jobs/{id}/progress",
                    Description = "Get progress of a music generation job. Stages: submitted (0.1), lyrics (0.33), composing (0.66), done (1.0)",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                },
                new()
                {
                    Method = "GET",
                    Path = "/music-gen/jobs/{id}/output",
                    Description = "Download generated MP3. Use ?clip=0 (default) or ?clip=1 for the second variation",
                    Returns = new ReturnSchema { ContentType = "audio/mpeg", Streaming = false, MediaCategory = "audio" }
                }
            },
            _ => new List<EndpointManifest>()
        };
    }
}
