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

                var endpoints = GetEndpointsForCapability(slug);

                capabilities.Add(new CapabilityManifest
                {
                    Slug = slug,
                    Type = entry.Definition.Type.ToString(),
                    DisplayName = entry.Definition.DisplayName,
                    Status = status.ToString(),
                    Provider = entry.ActiveProvider?.Name,
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

    private static List<EndpointManifest> GetEndpointsForCapability(string slug)
    {
        return slug switch
        {
            "tts" => new List<EndpointManifest>
            {
                new()
                {
                    Method = "POST",
                    Path = "/tts/generate",
                    Description = "Generate speech audio from text",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["text"] = new() { Type = "string", Required = true, Description = "Text to synthesize" },
                        ["voice"] = new() { Type = "string", Required = false, Default = "default", Description = "Voice identifier to use" },
                        ["speed"] = new() { Type = "number", Required = false, Default = 1.0, Min = 0.5, Max = 2.0, Description = "Playback speed multiplier" },
                        ["format"] = new() { Type = "string", Required = false, Default = "wav", Enum = ["wav", "mp3", "opus"], Description = "Output audio format" }
                    },
                    Returns = new ReturnSchema { ContentType = "audio/wav", Streaming = true }
                },
                new()
                {
                    Method = "GET",
                    Path = "/tts/voices",
                    Description = "List available voices for text-to-speech",
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
                    Description = "Generate an image from a text prompt",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["prompt"] = new() { Type = "string", Required = true, Description = "Text description of the image to generate" },
                        ["width"] = new() { Type = "integer", Required = false, Default = 1024, Min = 256, Max = 2048, Description = "Image width in pixels" },
                        ["height"] = new() { Type = "integer", Required = false, Default = 1024, Min = 256, Max = 2048, Description = "Image height in pixels" },
                        ["steps"] = new() { Type = "integer", Required = false, Default = 20, Min = 1, Max = 100, Description = "Number of diffusion steps" }
                    },
                    Returns = new ReturnSchema { ContentType = "image/png", Streaming = false }
                }
            },
            _ => new List<EndpointManifest>()
        };
    }
}
