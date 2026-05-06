using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Claude;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class DiscoverEndpoints
{
    private static ClaudeSessionService? _claudeService;

    public static void Map(WebApplication app, RedComputeConfig config, CapabilityRegistry registry, ClaudeSessionService? claudeService = null)
    {
        _claudeService = claudeService;
        MapRoutes(app, config, registry);
    }

    private static void MapRoutes(WebApplication app, RedComputeConfig config, CapabilityRegistry registry)
    {
        app.MapGet("/discover", async () =>
        {
            var capabilities = new List<CapabilityManifest>();

            foreach (var (slug, entry) in registry.Capabilities)
            {
                var defaultStatus = entry.ActiveProvider != null
                    ? await entry.ActiveProvider.GetStatusAsync()
                    : BackendStatus.Stopped;

                var endpoints = GetEndpointsForCapability(slug, registry, entry);

                var providerManifests = new List<ProviderManifest>();
                foreach (var (name, prov) in entry.Providers)
                {
                    var pStatus = await prov.GetStatusAsync();
                    var provType = entry.Config.Providers.TryGetValue(name, out var pc) ? pc.Type : "Unknown";
                    providerManifests.Add(new ProviderManifest
                    {
                        Name = name,
                        Type = provType,
                        Status = pStatus.ToString()
                    });
                }

                capabilities.Add(new CapabilityManifest
                {
                    Slug = slug,
                    Type = entry.Definition.Type.ToString(),
                    DisplayName = entry.Definition.DisplayName,
                    Status = defaultStatus.ToString(),
                    Provider = entry.ActiveProvider?.Name,
                    DefaultProvider = entry.DefaultProviderName,
                    Providers = providerManifests.Count > 0 ? providerManifests : null,
                    Sleeping = entry.IsSleeping,
                    Endpoints = endpoints
                });
            }

            return Results.Ok(new
            {
                service = "RedCompute",
                version = "0.2.0",
                apiBase = $"http://localhost:{config.ApiPort}",
                capabilities,
                management = new
                {
                    endpoints = new object[]
                    {
                        new { method = "GET", path = "/status", description = "Service status with capabilities and uptime" },
                        new { method = "GET", path = "/discover", description = "Full API discovery manifest with all endpoints" },
                        new { method = "GET", path = "/openapi.json", description = "OpenAPI 3.1 specification" },
                        new { method = "GET", path = "/jobs", description = "List jobs (query: capability, status, limit, offset)" },
                        new { method = "GET", path = "/jobs/{id}", description = "Get a specific job by ID" },
                        new { method = "DELETE", path = "/jobs/{id}", description = "Cancel a job" },
                        new { method = "DELETE", path = "/jobs/cleanup", description = "Delete old jobs and logs (query: olderThanDays, default 30)" },
                        new { method = "GET", path = "/jobs/{id}/logs", description = "Get logs for a specific job" },
                        new { method = "GET", path = "/activity", description = "Job activity histogram (query: window in hours)" },
                        new { method = "GET", path = "/logs", description = "Query logs (tag, search, since, until, jobId, level, limit, offset)" },
                        new { method = "GET", path = "/logs/tags", description = "List log tags with counts" },
                        new { method = "GET", path = "/logs/summary", description = "Log summary statistics" },
                        new { method = "POST", path = "/control/start/{slug}", description = "Start the default provider's backend" },
                        new { method = "POST", path = "/control/stop/{slug}", description = "Stop the default provider's backend" },
                        new { method = "POST", path = "/control/start/{slug}/{provider}", description = "Start a specific provider's backend" },
                        new { method = "POST", path = "/control/stop/{slug}/{provider}", description = "Stop a specific provider's backend" },
                        new { method = "POST", path = "/control/sleep/{slug}", description = "Put a capability to sleep (stop after idle)" },
                        new { method = "POST", path = "/control/wake/{slug}", description = "Wake a sleeping capability" },
                        new { method = "GET", path = "/settings", description = "Current service configuration (API keys masked)" },
                        new { method = "PUT", path = "/settings/general", description = "Update general settings: apiPort, logLevel, autoStartWithWindows, tunnel*" },
                        new { method = "PUT", path = "/settings/capability/{slug}", description = "Update capability config: enabled, activeProvider" },
                        new { method = "PUT", path = "/settings/capability/{slug}/provider/{name}", description = "Update provider settings: model, ports, paths, API keys, extras" },
                        new { method = "GET", path = "/tunnel/status", description = "Cloudflare tunnel status, hostname, and error" },
                        new { method = "POST", path = "/tunnel/start", description = "Start the Cloudflare tunnel" },
                        new { method = "POST", path = "/tunnel/stop", description = "Stop the Cloudflare tunnel" },
                        new { method = "GET", path = "/ws/schema", description = "WebSocket event schema — discover event types and data shapes" }
                    },
                    websocket = new
                    {
                        url = $"ws://localhost:{config.ApiPort}/ws",
                        description = "Real-time event stream. Events: job.created, job.updated, log.entry, capability.status, claude.session.created, claude.session.updated, claude.session.ended, claude.stream",
                        schemaEndpoint = "/ws/schema"
                    },
                    dashboard = new
                    {
                        url = $"http://localhost:{config.ApiPort}/",
                        description = "Web dashboard UI (served from the same port as the API)"
                    }
                }
            });
        });
    }

    private static List<EndpointManifest> GetEndpointsForCapability(string slug, CapabilityRegistry registry, CapabilityEntry entry)
    {
        ParameterSchema? providerParam = entry.Providers.Count > 1
            ? new() { Type = "string", Required = false, Default = entry.DefaultProviderName, Enum = entry.Providers.Keys.ToList(), Description = "Provider to use for this request. Defaults to the configured default provider." }
            : null;
        var result = slug switch
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
                    Description = "Transcribe audio to text. Accepts multipart/form-data (file upload) or application/json (base64-encoded audio). Returns full transcription with timed segments. Use stream=true for NDJSON segment streaming, ?async=true for background processing.",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["audio"] = new() { Type = "file", Required = true, Description = "Audio file upload (wav, mp3, opus, flac, ogg, webm). Use multipart/form-data." },
                        ["audio_base64"] = new() { Type = "string", Required = false, Description = "Alternative: base64-encoded audio in JSON body (use application/json content type instead of multipart)" },
                        ["language"] = new() { Type = "string", Required = false, Default = "auto", Description = "ISO 639-1 language code (e.g. 'en', 'fr', 'ja') or 'auto' for detection. GET /stt/languages for full list." },
                        ["task"] = new() { Type = "string", Required = false, Default = "transcribe", Enum = ["transcribe", "translate"], Description = "'transcribe' preserves source language, 'translate' outputs English" },
                        ["word_timestamps"] = new() { Type = "boolean", Required = false, Default = false, Description = "Include word-level start/end times and confidence in each segment" },
                        ["stream"] = new() { Type = "boolean", Required = false, Default = false, Description = "Stream segments as NDJSON lines as they are decoded. Each line is a JSON object with type='segment' or type='result'." },
                        ["initial_prompt"] = new() { Type = "string", Required = false, Description = "Conditioning text for the model — use for vocabulary hints, acronyms, or prior context" },
                        ["vad_filter"] = new() { Type = "boolean", Required = false, Default = true, Description = "Apply Silero Voice Activity Detection to skip silence regions" }
                    },
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false, OutputEndpoint = "/stt/jobs/{id}/output" }
                },
                new()
                {
                    Method = "GET",
                    Path = "/stt/jobs/{id}/output",
                    Description = "Download the transcription result JSON for a completed STT job",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                },
                new()
                {
                    Method = "GET",
                    Path = "/stt/models",
                    Description = "List available Whisper models with parameter count, VRAM requirements, and relative speed. Shows which model is currently loaded.",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                },
                new()
                {
                    Method = "GET",
                    Path = "/stt/languages",
                    Description = "List all supported languages with ISO 639-1 codes. Use these codes in the 'language' parameter of /stt/transcribe.",
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
            "ai-session" => new List<EndpointManifest>
            {
                new()
                {
                    Method = "POST",
                    Path = "/ai-session/generate",
                    Description = "Unified AI endpoint. mode=session (default): start a coding session in a project. mode=oneshot: fast, stateless LLM inference for summarization, classification, etc.",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["mode"] = new()
                        {
                            Type = "string",
                            Required = false,
                            Default = "session",
                            Enum = ["session", "oneshot"],
                            Description = "'session' starts a persistent coding session; 'oneshot' runs a single stateless prompt"
                        },
                        ["project"] = new()
                        {
                            Type = "string",
                            Required = false,
                            Description = "(session mode) Project name to start the session in",
                            Enum = _claudeService?.ListProjects().Select(p => p.Name).ToList()
                        },
                        ["prompt"] = new()
                        {
                            Type = "string",
                            Required = false,
                            Description = "(session mode) Initial message to send after session starts"
                        },
                        ["model"] = new() { Type = "string", Required = false, Default = "haiku", Description = "(oneshot mode) Model alias. GET /ai-session/models for options." },
                        ["system"] = new() { Type = "string", Required = false, Description = "(oneshot mode) System prompt to set behavior and context" },
                        ["messages"] = new() { Type = "array", Required = false, Description = "(oneshot mode) Array of {role, content} message objects. Required for oneshot." },
                        ["maxTokens"] = new() { Type = "integer", Required = false, Default = 1024, Min = 1, Max = 8192, Description = "(oneshot mode) Maximum tokens to generate" }
                    },
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = true }
                },
                new()
                {
                    Method = "GET",
                    Path = "/ai-session/models",
                    Description = "List available LLM models with their default and speed characteristics (for oneshot mode)",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                },
                new()
                {
                    Method = "GET",
                    Path = "/claude/projects",
                    Description = "List available projects that can host AI sessions"
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions",
                    Description = "Start a new session by project path",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["projectPath"] = new() { Type = "string", Required = true, Description = "Full path to the project directory" }
                    }
                },
                new()
                {
                    Method = "GET",
                    Path = "/claude/sessions",
                    Description = "List all active and recent AI sessions"
                },
                new()
                {
                    Method = "GET",
                    Path = "/claude/sessions/{id}",
                    Description = "Get session details and full message history"
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions/{id}/message",
                    Description = "Send a message or images to an active session",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["content"] = new() { Type = "string", Required = false, Description = "Message text" },
                        ["images"] = new() { Type = "array", Required = false, Description = "Array of {mediaType, base64} image attachments" }
                    }
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions/{id}/interrupt",
                    Description = "Interrupt the currently running operation in a session"
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions/{id}/stop",
                    Description = "Stop a session gracefully"
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions/{id}/resume",
                    Description = "Resume a previously stopped session"
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions/{id}/dismiss",
                    Description = "Mark a stopped session as dismissed (hide from listings)"
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions/{id}/config",
                    Description = "Update session model and effort configuration (restarts the session)",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["model"] = new() { Type = "string", Required = false, Description = "Model alias: sonnet, opus, haiku", Enum = ["sonnet", "opus", "haiku"] },
                        ["effort"] = new() { Type = "string", Required = false, Description = "Reasoning effort level", Enum = ["low", "medium", "high", "xhigh", "max"] }
                    }
                },
                new()
                {
                    Method = "POST",
                    Path = "/claude/sessions/{id}/permission-mode",
                    Description = "Set the permission mode for tool execution",
                    Parameters = new Dictionary<string, ParameterSchema>
                    {
                        ["mode"] = new() { Type = "string", Required = true, Description = "Permission mode", Enum = ["bypassPermissions", "plan", "default", "acceptEdits", "auto"] }
                    }
                },
                new()
                {
                    Method = "DELETE",
                    Path = "/claude/sessions/{id}",
                    Description = "Force-kill a session process immediately"
                }
            },
            _ => new List<EndpointManifest>()
        };

        if (providerParam != null)
        {
            foreach (var ep in result)
            {
                if (ep.Method == "POST" && (ep.Path.EndsWith("/generate") || ep.Path.EndsWith("/transcribe")) && ep.Parameters != null)
                    ep.Parameters["provider"] = providerParam;
            }
        }

        return result;
    }
}
