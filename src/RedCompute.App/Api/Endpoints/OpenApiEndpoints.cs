using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Api.Endpoints;

public static class OpenApiEndpoints
{
    public static void Map(WebApplication app, RedComputeConfig config, CapabilityRegistry registry)
    {
        app.MapGet("/openapi.json", () =>
        {
            var spec = BuildSpec(config, registry);
            return Results.Text(
                JsonSerializer.Serialize(spec, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }),
                "application/json");
        });
    }

    private static object BuildSpec(RedComputeConfig config, CapabilityRegistry registry)
    {
        var paths = new Dictionary<string, object>
        {
            ["/discover"] = new Dictionary<string, object>
            {
                ["get"] = Op("Discover", "Returns a machine-readable manifest of all capabilities, parameters, and live status", "application/json",
                    Schema("object", "ServiceManifest"))
            },
            ["/status"] = new Dictionary<string, object>
            {
                ["get"] = Op("GetStatus", "Service health: uptime, capability states, providers", "application/json",
                    Schema("object", "StatusResponse"))
            },
            ["/jobs"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "ListJobs",
                    ["summary"] = "Paginated job history with full-text search. Returns { items, total } for pagination.",
                    ["parameters"] = new object[]
                    {
                        QueryParam("capability", "string", "Filter by capability slug (e.g. tts, stt, image-gen, music-gen, ai-session)"),
                        QueryParam("status", "string", "Filter by job status (Queued, Running, Completed, Failed, Cancelled)"),
                        QueryParam("caller", "string", "Filter by callerInfo (exact match — identifies who queued the job)"),
                        QueryParam("search", "string", "Case-insensitive text search across job name, providerName, callerInfo, and capabilitySlug"),
                        QueryParam("limit", "integer", "Max results per page (default 50)"),
                        QueryParam("offset", "integer", "Skip N results for pagination (use with total to page through results)")
                    },
                    ["responses"] = Responses("application/json", new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["description"] = "Paginated result with total count for building pagination",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["items"] = new Dictionary<string, object>
                            {
                                ["type"] = "array",
                                ["items"] = new { @ref = "#/components/schemas/JobRecord" }
                            },
                            ["total"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Total matching records before pagination. Compare with offset+items.length to determine if more pages exist." }
                        },
                        ["required"] = new[] { "items", "total" }
                    })
                }
            },
            ["/jobs/{id}"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "GetJob",
                    ["summary"] = "Get full details of a single job including input, output, and error details",
                    ["parameters"] = new object[] { PathParam("id", "string", "Job UUID") },
                    ["responses"] = Responses("application/json", new { @ref = "#/components/schemas/JobRecord" })
                },
                ["delete"] = new Dictionary<string, object>
                {
                    ["operationId"] = "CancelJob",
                    ["summary"] = "Cancel a queued or running job",
                    ["parameters"] = new object[] { PathParam("id", "string", "Job UUID") },
                    ["responses"] = Responses("application/json", Schema("object", "CancelResult"))
                }
            },
            ["/control/start/{slug}"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "StartCapability",
                    ["summary"] = "Start the default provider's backend for a capability",
                    ["parameters"] = new object[] { PathParam("slug", "string", "Capability slug (tts, stt, image-gen, etc.)") },
                    ["responses"] = Responses("application/json", Schema("object", "ControlResult"))
                }
            },
            ["/control/stop/{slug}"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "StopCapability",
                    ["summary"] = "Stop the default provider's backend for a capability",
                    ["parameters"] = new object[] { PathParam("slug", "string", "Capability slug") },
                    ["responses"] = Responses("application/json", Schema("object", "ControlResult"))
                }
            },
            ["/control/start/{slug}/{provider}"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "StartCapabilityProvider",
                    ["summary"] = "Start a specific provider's backend for a capability",
                    ["parameters"] = new object[]
                    {
                        PathParam("slug", "string", "Capability slug (tts, stt, image-gen, etc.)"),
                        PathParam("provider", "string", "Provider name (e.g. local-wsl, comfyui, suno)")
                    },
                    ["responses"] = Responses("application/json", Schema("object", "ControlResult"))
                }
            },
            ["/control/stop/{slug}/{provider}"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "StopCapabilityProvider",
                    ["summary"] = "Stop a specific provider's backend for a capability",
                    ["parameters"] = new object[]
                    {
                        PathParam("slug", "string", "Capability slug"),
                        PathParam("provider", "string", "Provider name")
                    },
                    ["responses"] = Responses("application/json", Schema("object", "ControlResult"))
                }
            },
            ["/settings"] = new Dictionary<string, object>
            {
                ["get"] = Op("GetSettings", "Current service configuration (API keys masked)", "application/json",
                    Schema("object", "SettingsResponse"))
            },
            ["/settings/general"] = new Dictionary<string, object>
            {
                ["put"] = new Dictionary<string, object>
                {
                    ["operationId"] = "UpdateGeneralSettings",
                    ["summary"] = "Update general settings (apiPort, logLevel, autoStartWithWindows)",
                    ["requestBody"] = RequestBody(new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["apiPort"] = Prop("integer", "API listen port"),
                            ["logLevel"] = PropEnum("Log verbosity", "Info", "Debug", "Info", "Warning", "Error"),
                            ["autoStartWithWindows"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Start RedCompute on Windows login" }
                        }
                    }),
                    ["responses"] = Responses("application/json", Schema("object", "SettingsUpdateResult"))
                }
            },
            ["/settings/capability/{slug}"] = new Dictionary<string, object>
            {
                ["put"] = new Dictionary<string, object>
                {
                    ["operationId"] = "UpdateCapabilitySettings",
                    ["summary"] = "Update capability config (enabled, activeProvider)",
                    ["parameters"] = new object[] { PathParam("slug", "string", "Capability slug") },
                    ["requestBody"] = RequestBody(new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["enabled"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Enable or disable this capability" },
                            ["activeProvider"] = Prop("string", "Provider key to activate (must exist in providers dict)")
                        }
                    }),
                    ["responses"] = Responses("application/json", Schema("object", "CapabilitySettingsUpdateResult"))
                }
            },
            ["/ws/schema"] = new Dictionary<string, object>
            {
                ["get"] = Op("GetWebSocketSchema", "WebSocket event schema — lists all event types, their data shapes, and field names. Connect at ws://host:port/ws", "application/json",
                    Schema("object", "WebSocketSchema"))
            }
        };

        // Add per-capability endpoints
        if (registry.Capabilities.ContainsKey("tts"))
        {
            paths["/tts/generate"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "TtsGenerate",
                    ["summary"] = "Generate speech audio from text",
                    ["requestBody"] = RequestBody(BuildGenerateBody(registry, "tts", new Dictionary<string, object>
                    {
                        ["text"] = Prop("string", "Text to synthesize"),
                        ["voice"] = PropEnum("Speaker voice name", "Serena", TtsVoiceDiscovery.AllVoices(registry).ToArray()),
                        ["language"] = PropEnum("Language for synthesis", "English", "English", "Chinese", "French", "Japanese", "Korean"),
                        ["emotion"] = PropEnum("Emotional tone", "neutral", "neutral", "excited", "happy", "sad", "angry", "sarcastic", "curious", "confident"),
                        ["instruct"] = Prop("string", "Natural language instruction for voice style (overrides emotion)"),
                        ["speed"] = PropNum("Playback speed multiplier", 1.0, 0.5, 2.0),
                        ["stream"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "If true, streams raw PCM audio (s16le, 24kHz, mono) instead of buffered WAV", ["default"] = false }
                    }, new[] { "text" })),
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Audio data. Content-Type is audio/wav (default) or audio/pcm (when stream=true, with X-Audio-Sample-Rate/X-Audio-Channels/X-Audio-Format headers)",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["audio/wav"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new { type = "string", format = "binary" }
                                },
                                ["audio/pcm"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new { type = "string", format = "binary" }
                                }
                            }
                        },
                        ["404"] = ErrorResponse("Provider not found (when explicit provider is requested)"),
                        ["422"] = ErrorResponse("Validation failed — field-level errors in 'fields'"),
                        ["503"] = ErrorResponse("Provider not running")
                    }
                }
            };
            paths["/tts/voices"] = new Dictionary<string, object>
            {
                ["get"] = Op("TtsListVoices", "List available TTS voices", "application/json",
                    Schema("object", "VoiceList"))
            };
        }

        if (registry.Capabilities.ContainsKey("stt"))
        {
            var sttMultipartProps = new Dictionary<string, object>
            {
                ["audio"] = new { type = "string", format = "binary", description = "Audio file (wav, mp3, opus, flac, ogg, webm)" },
                ["language"] = Prop("string", "ISO 639-1 language code or 'auto' for detection", "auto"),
                ["task"] = PropEnum("Transcription task", "transcribe", "transcribe", "translate"),
                ["word_timestamps"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Include word-level timestamps in output", ["default"] = false },
                ["stream"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Stream segments as NDJSON lines as they are decoded", ["default"] = false },
                ["initial_prompt"] = Prop("string", "Optional text to condition the model (vocabulary hints, prior context)"),
                ["vad_filter"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Apply Silero VAD to skip silence", ["default"] = true }
            };
            var sttJsonProps = new Dictionary<string, object>
            {
                ["audio_base64"] = Prop("string", "Base64-encoded audio data"),
                ["audio_content_type"] = Prop("string", "MIME type of the encoded audio", "audio/wav"),
                ["language"] = Prop("string", "ISO 639-1 language code or 'auto'", "auto"),
                ["task"] = PropEnum("Transcription task", "transcribe", "transcribe", "translate"),
                ["word_timestamps"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Include word-level timestamps", ["default"] = false },
                ["stream"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Stream segments as NDJSON", ["default"] = false },
                ["initial_prompt"] = Prop("string", "Optional conditioning text"),
                ["vad_filter"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Apply Silero VAD", ["default"] = true }
            };

            var sttEntry = registry.Get("stt");
            if (sttEntry != null && sttEntry.Providers.Count > 1)
            {
                var providerProp = PropEnum(
                    "Provider to use. Defaults to the configured default provider.",
                    sttEntry.DefaultProviderName ?? "",
                    sttEntry.Providers.Keys.ToArray());
                sttMultipartProps["provider"] = providerProp;
                sttJsonProps["provider"] = providerProp;
            }

            paths["/stt/transcribe"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "SttTranscribe",
                    ["summary"] = "Transcribe audio to text. Accepts multipart/form-data (file upload) or application/json (base64). Sync by default, async with ?async=true, streaming segments with stream=true.",
                    ["parameters"] = new object[]
                    {
                        QueryParam("async", "boolean", "Return 202 with job ID instead of waiting for result")
                    },
                    ["requestBody"] = new Dictionary<string, object>
                    {
                        ["required"] = true,
                        ["content"] = new Dictionary<string, object>
                        {
                            ["multipart/form-data"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["required"] = new[] { "audio" },
                                    ["properties"] = sttMultipartProps
                                }
                            },
                            ["application/json"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["required"] = new[] { "audio_base64" },
                                    ["properties"] = sttJsonProps
                                }
                            }
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Transcription result (sync mode). Content-Type is application/json (default) or application/x-ndjson (when stream=true)",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new Dictionary<string, object>
                                        {
                                            ["text"] = Prop("string", "Full transcription text"),
                                            ["language"] = Prop("string", "Detected language (ISO 639-1)"),
                                            ["language_probability"] = PropNum("Detection confidence", 0, 0, 1),
                                            ["duration_seconds"] = Prop("number", "Audio duration in seconds"),
                                            ["processing_seconds"] = Prop("number", "Processing time in seconds"),
                                            ["segments"] = new Dictionary<string, object>
                                            {
                                                ["type"] = "array",
                                                ["description"] = "Timed transcript segments",
                                                ["items"] = new Dictionary<string, object>
                                                {
                                                    ["type"] = "object",
                                                    ["properties"] = new Dictionary<string, object>
                                                    {
                                                        ["start"] = Prop("number", "Segment start time in seconds"),
                                                        ["end"] = Prop("number", "Segment end time in seconds"),
                                                        ["text"] = Prop("string", "Segment text"),
                                                        ["avg_logprob"] = Prop("number", "Average log probability"),
                                                        ["no_speech_prob"] = Prop("number", "Probability of no speech"),
                                                        ["words"] = new Dictionary<string, object>
                                                        {
                                                            ["type"] = "array",
                                                            ["description"] = "Word-level timestamps (only when word_timestamps=true)",
                                                            ["items"] = new Dictionary<string, object>
                                                            {
                                                                ["type"] = "object",
                                                                ["properties"] = new Dictionary<string, object>
                                                                {
                                                                    ["start"] = Prop("number", "Word start time"),
                                                                    ["end"] = Prop("number", "Word end time"),
                                                                    ["word"] = Prop("string", "Word text"),
                                                                    ["probability"] = Prop("number", "Word confidence")
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                },
                                ["application/x-ndjson"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "object",
                                        ["description"] = "NDJSON stream: each line is a segment object with type='segment', final line has type='result' with full text and metadata"
                                    }
                                }
                            }
                        },
                        ["202"] = new Dictionary<string, object>
                        {
                            ["description"] = "Job accepted (async mode)",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new Dictionary<string, object>
                                        {
                                            ["jobId"] = new { type = "string", format = "uuid" },
                                            ["status"] = new { type = "string" }
                                        }
                                    }
                                }
                            }
                        },
                        ["404"] = ErrorResponse("Provider not found (when explicit provider is requested)"),
                        ["415"] = ErrorResponse("Unsupported Content-Type"),
                        ["422"] = ErrorResponse("Validation failed — field-level errors in 'fields'"),
                        ["503"] = ErrorResponse("Provider not running")
                    }
                }
            };
            paths["/stt/jobs/{id}/output"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "SttJobOutput",
                    ["summary"] = "Download the transcription result for a completed STT job",
                    ["parameters"] = new object[] { PathParam("id", "string", "Job UUID") },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Transcription JSON",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new { type = "object" }
                                }
                            }
                        },
                        ["409"] = ErrorResponse("Job still running"),
                        ["404"] = ErrorResponse("Job or output not found")
                    }
                }
            };
            paths["/stt/models"] = new Dictionary<string, object>
            {
                ["get"] = Op("SttListModels", "List available Whisper models with size, VRAM requirements, and relative speed. Shows which model is currently loaded.", "application/json",
                    new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["models"] = new Dictionary<string, object>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["properties"] = new Dictionary<string, object>
                                    {
                                        ["name"] = Prop("string", "Model identifier"),
                                        ["parameters"] = Prop("string", "Parameter count"),
                                        ["vram_gb"] = Prop("number", "Approximate VRAM usage in GB"),
                                        ["relative_speed"] = Prop("integer", "Speed relative to large-v3 (higher = faster)")
                                    }
                                }
                            },
                            ["current"] = Prop("string", "Currently loaded model name")
                        }
                    })
            };
            paths["/stt/languages"] = new Dictionary<string, object>
            {
                ["get"] = Op("SttListLanguages", "List all supported languages with ISO 639-1 codes. Use these codes in the 'language' parameter.", "application/json",
                    new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["languages"] = new Dictionary<string, object>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["properties"] = new Dictionary<string, object>
                                    {
                                        ["code"] = Prop("string", "ISO 639-1 language code"),
                                        ["name"] = Prop("string", "Language name in English")
                                    }
                                }
                            }
                        }
                    })
            };
        }

        paths["/ai-session/generate"] = new Dictionary<string, object>
        {
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "AiSessionGenerate",
                ["summary"] = "Unified AI endpoint. mode=session: start a coding session. mode=oneshot: fast, stateless LLM inference.",
                ["requestBody"] = RequestBody(new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["mode"] = PropEnum("'session' (default) for persistent coding sessions, 'oneshot' for stateless prompts", "session", "session", "oneshot"),
                        ["project"] = Prop("string", "(session) Project name to start the session in"),
                        ["prompt"] = Prop("string", "(session) Initial message to send after session starts"),
                        ["model"] = Prop("string", "(oneshot) Model alias", "haiku"),
                        ["system"] = Prop("string", "(oneshot) System prompt to set behavior and context"),
                        ["messages"] = new Dictionary<string, object>
                        {
                            ["type"] = "array",
                            ["description"] = "(oneshot) Array of {role, content} message objects. Required for oneshot mode.",
                            ["items"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["required"] = new[] { "role", "content" },
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["role"] = PropEnum("Message role", "user", "user", "assistant"),
                                    ["content"] = Prop("string", "Message text content")
                                }
                            }
                        },
                        ["maxTokens"] = new Dictionary<string, object>
                        {
                            ["type"] = "integer", ["description"] = "(oneshot) Maximum tokens to generate",
                            ["default"] = 1024, ["minimum"] = 1, ["maximum"] = 8192
                        }
                    }
                }),
                ["responses"] = new Dictionary<string, object>
                {
                    ["200"] = new Dictionary<string, object>
                    {
                        ["description"] = "Oneshot: generated text with token usage. Session: accepted with session ID.",
                        ["content"] = new Dictionary<string, object>
                        {
                            ["application/json"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["properties"] = new Dictionary<string, object>
                                    {
                                        ["text"] = Prop("string", "(oneshot) Generated text"),
                                        ["model"] = Prop("string", "(oneshot) Model used for generation"),
                                        ["inputTokens"] = Prop("integer", "(oneshot) Input tokens consumed"),
                                        ["outputTokens"] = Prop("integer", "(oneshot) Output tokens generated"),
                                        ["sessionId"] = Prop("string", "(session) Created session ID"),
                                        ["jobId"] = Prop("string", "(session) Associated job ID")
                                    }
                                }
                            }
                        }
                    },
                    ["422"] = ErrorResponse("Validation failed"),
                    ["502"] = ErrorResponse("Execution error"),
                    ["503"] = ErrorResponse("Service unavailable")
                }
            }
        };
        paths["/ai-session/models"] = new Dictionary<string, object>
        {
            ["get"] = Op("AiSessionListModels", "List available LLM models with speed characteristics and default model (for oneshot mode)", "application/json",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["models"] = new Dictionary<string, object>
                        {
                            ["type"] = "array",
                            ["items"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["id"] = Prop("string", "Model identifier"),
                                    ["name"] = Prop("string", "Display name"),
                                    ["fast"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Whether this model is optimized for speed" }
                                }
                            }
                        },
                        ["default"] = Prop("string", "Default model ID")
                    }
                })
        };

        if (registry.Capabilities.ContainsKey("image-gen"))
        {
            paths["/image-gen/generate"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "ImageGenGenerate",
                    ["summary"] = "Generate image/video from text prompt via ComfyUI. Sync by default, async with ?async=true",
                    ["parameters"] = new object[]
                    {
                        QueryParam("async", "boolean", "Return 202 with job ID instead of waiting for result")
                    },
                    ["requestBody"] = RequestBody(BuildGenerateBody(registry, "image-gen", new Dictionary<string, object>
                    {
                        ["prompt"] = Prop("string", "Text description of the image to generate"),
                        ["workflow"] = Prop("string", "Workflow name (GET /image-gen/workflows for options)", "z_turbo"),
                        ["negative"] = Prop("string", "Negative prompt (things to avoid)", ""),
                        ["seed"] = Prop("integer", "Random seed for reproducibility"),
                        ["width"] = Prop("integer", "Image width in pixels (workflow-dependent)"),
                        ["height"] = Prop("integer", "Image height in pixels (workflow-dependent)"),
                        ["image_url"] = Prop("string", "Source image URL for img2img or video workflows")
                    }, new[] { "prompt" })),
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Generated image (sync mode)",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["image/png"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new { type = "string", format = "binary" }
                                }
                            }
                        },
                        ["202"] = new Dictionary<string, object>
                        {
                            ["description"] = "Job accepted (async mode)",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new Dictionary<string, object>
                                        {
                                            ["jobId"] = new { type = "string", format = "uuid" },
                                            ["status"] = new { type = "string" }
                                        }
                                    }
                                }
                            }
                        },
                        ["404"] = ErrorResponse("Provider not found"),
                        ["422"] = ErrorResponse("Validation failed"),
                        ["503"] = ErrorResponse("Provider not running")
                    }
                }
            };
            paths["/image-gen/workflows"] = new Dictionary<string, object>
            {
                ["get"] = Op("ImageGenListWorkflows", "List available ComfyUI workflows with their parameters", "application/json",
                    Schema("object", "WorkflowList"))
            };
            paths["/image-gen/workflows/{name}"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "ImageGenGetWorkflow",
                    ["summary"] = "Get details of a specific workflow",
                    ["parameters"] = new object[] { PathParam("name", "string", "Workflow name") },
                    ["responses"] = Responses("application/json", Schema("object", "WorkflowDefinition"))
                }
            };
            paths["/image-gen/jobs/{id}/progress"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "ImageGenJobProgress",
                    ["summary"] = "Get real-time progress of an image generation job",
                    ["parameters"] = new object[] { PathParam("id", "string", "Job UUID") },
                    ["responses"] = Responses("application/json", new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["id"] = new { type = "string", format = "uuid" },
                            ["status"] = new { type = "string" },
                            ["progress"] = new { type = "number", minimum = 0, maximum = 1 },
                            ["errorMessage"] = new { type = "string" }
                        }
                    })
                }
            };
            paths["/image-gen/jobs/{id}/output"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "ImageGenJobOutput",
                    ["summary"] = "Download the generated image/video",
                    ["parameters"] = new object[] { PathParam("id", "string", "Job UUID") },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Generated file",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["image/png"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new { type = "string", format = "binary" }
                                }
                            }
                        },
                        ["409"] = ErrorResponse("Job still running"),
                        ["404"] = ErrorResponse("Job or output not found")
                    }
                }
            };
        }

        if (registry.Capabilities.ContainsKey("music-gen"))
        {
            paths["/music-gen/generate"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "MusicGenGenerate",
                    ["summary"] = "Generate music via Suno AI. Sync by default, async with ?async=true. Returns 2 variations.",
                    ["parameters"] = new object[]
                    {
                        QueryParam("async", "boolean", "Return 202 with job ID instead of waiting for result")
                    },
                    ["requestBody"] = RequestBody(BuildGenerateBody(registry, "music-gen", new Dictionary<string, object>
                    {
                        ["prompt"] = Prop("string", "Musical description — mood, instruments, lyrics"),
                        ["style"] = Prop("string", "Genre/style tags", ""),
                        ["title"] = Prop("string", "Track title", ""),
                        ["instrumental"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Instrumental only (no vocals)", ["default"] = true }
                    }, new[] { "prompt" })),
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Generated MP3 audio (sync mode)",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["audio/mpeg"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new { type = "string", format = "binary" }
                                }
                            }
                        },
                        ["202"] = new Dictionary<string, object>
                        {
                            ["description"] = "Job accepted (async mode)",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new Dictionary<string, object>
                                        {
                                            ["jobId"] = new { type = "string", format = "uuid" },
                                            ["status"] = new { type = "string" }
                                        }
                                    }
                                }
                            }
                        },
                        ["404"] = ErrorResponse("Provider not found"),
                        ["422"] = ErrorResponse("Validation failed"),
                        ["503"] = ErrorResponse("Provider not running")
                    }
                }
            };
            paths["/music-gen/jobs/{id}/progress"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "MusicGenJobProgress",
                    ["summary"] = "Get progress of a music generation job",
                    ["parameters"] = new object[] { PathParam("id", "string", "Job UUID") },
                    ["responses"] = Responses("application/json", new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["id"] = new { type = "string", format = "uuid" },
                            ["status"] = new { type = "string" },
                            ["progress"] = new { type = "number", minimum = 0, maximum = 1 },
                            ["errorMessage"] = new { type = "string" }
                        }
                    })
                }
            };
            paths["/music-gen/jobs/{id}/output"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "MusicGenJobOutput",
                    ["summary"] = "Download generated MP3. Use ?clip=1 for second variation.",
                    ["parameters"] = new object[]
                    {
                        PathParam("id", "string", "Job UUID"),
                        QueryParam("clip", "integer", "Clip index: 0 (default) or 1 for second variation")
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "MP3 audio file",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["audio/mpeg"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new { type = "string", format = "binary" }
                                }
                            }
                        },
                        ["409"] = ErrorResponse("Job still running"),
                        ["404"] = ErrorResponse("Job or output not found")
                    }
                }
            };
        }

        // AI Session (Claude Code) endpoints
        paths["/ping"] = new Dictionary<string, object>
        {
            ["get"] = Op("Ping", "Health check — no auth required. Returns { ok: true, version }", "application/json",
                new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["ok"] = new { type = "boolean" }, ["version"] = new { type = "string" } } })
        };
        paths["/claude/projects"] = new Dictionary<string, object>
        {
            ["get"] = Op("ClaudeListProjects", "List available projects that can host AI sessions", "application/json",
                new Dictionary<string, object> { ["type"] = "array", ["items"] = new { @ref = "#/components/schemas/ProjectInfo" } })
        };
        paths["/claude/sessions"] = new Dictionary<string, object>
        {
            ["get"] = Op("ClaudeListSessions", "List all active and recent AI sessions", "application/json",
                new Dictionary<string, object> { ["type"] = "array", ["items"] = new { @ref = "#/components/schemas/ClaudeSessionInfo" } }),
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeStartSession",
                ["summary"] = "Start a new AI coding session in a project directory",
                ["requestBody"] = RequestBody(new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["required"] = new[] { "projectPath" },
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["projectPath"] = Prop("string", "Full path to the project directory")
                    }
                }),
                ["responses"] = Responses("application/json", new { @ref = "#/components/schemas/ClaudeSessionInfo" })
            }
        };
        paths["/claude/sessions/{id}"] = new Dictionary<string, object>
        {
            ["get"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeGetSession",
                ["summary"] = "Get session details and full message history",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["responses"] = Responses("application/json", new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["session"] = new { @ref = "#/components/schemas/ClaudeSessionInfo" },
                        ["messages"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new { @ref = "#/components/schemas/ClaudeMessage" } }
                    }
                })
            },
            ["delete"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeForceKill",
                ["summary"] = "Force-kill a session process immediately",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["responses"] = Responses("application/json", new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["killed"] = new { type = "boolean" } } })
            }
        };
        paths["/claude/sessions/by-job/{jobId}"] = new Dictionary<string, object>
        {
            ["get"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeGetSessionByJob",
                ["summary"] = "Look up session by its linked job ID",
                ["parameters"] = new object[] { PathParam("jobId", "string", "Job ID (GUID)") },
                ["responses"] = Responses("application/json", new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["session"] = new { @ref = "#/components/schemas/ClaudeSessionInfo" },
                        ["messages"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new { @ref = "#/components/schemas/ClaudeMessage" } }
                    }
                })
            }
        };
        paths["/claude/sessions/{id}/message"] = new Dictionary<string, object>
        {
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeSendMessage",
                ["summary"] = "Send a message or images to an active session",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["requestBody"] = RequestBody(new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["content"] = Prop("string", "Message text"),
                        ["images"] = new Dictionary<string, object> { ["type"] = "array", ["description"] = "Image attachments", ["items"] = new { @ref = "#/components/schemas/ImageAttachment" } }
                    }
                }),
                ["responses"] = Responses("application/json", new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["sent"] = new { type = "boolean" } } })
            }
        };
        paths["/claude/sessions/{id}/interrupt"] = new Dictionary<string, object>
        {
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeInterrupt",
                ["summary"] = "Interrupt the currently running operation",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["responses"] = Responses("application/json", new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["interrupted"] = new { type = "boolean" } } })
            }
        };
        paths["/claude/sessions/{id}/stop"] = new Dictionary<string, object>
        {
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeStop",
                ["summary"] = "Stop a session gracefully",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["responses"] = Responses("application/json", new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["stopped"] = new { type = "boolean" } } })
            }
        };
        paths["/claude/sessions/{id}/resume"] = new Dictionary<string, object>
        {
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeResume",
                ["summary"] = "Resume a previously stopped session",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["responses"] = Responses("application/json", new { @ref = "#/components/schemas/ClaudeSessionInfo" })
            }
        };
        paths["/claude/sessions/{id}/config"] = new Dictionary<string, object>
        {
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeUpdateConfig",
                ["summary"] = "Update model and effort (restarts the session process)",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["requestBody"] = RequestBody(new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["model"] = PropEnum("Model alias", "opus", "sonnet", "opus", "haiku"),
                        ["effort"] = PropEnum("Reasoning effort", "high", "low", "medium", "high", "xhigh", "max")
                    }
                }),
                ["responses"] = Responses("application/json", new { @ref = "#/components/schemas/ClaudeSessionInfo" })
            }
        };
        paths["/claude/sessions/{id}/permission-mode"] = new Dictionary<string, object>
        {
            ["post"] = new Dictionary<string, object>
            {
                ["operationId"] = "ClaudeSetPermissionMode",
                ["summary"] = "Set the permission mode for tool execution",
                ["parameters"] = new object[] { PathParam("id", "string", "Session ID") },
                ["requestBody"] = RequestBody(new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["required"] = new[] { "mode" },
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["mode"] = PropEnum("Permission mode", "bypassPermissions", "bypassPermissions", "plan", "default", "acceptEdits", "auto")
                    }
                }),
                ["responses"] = Responses("application/json", new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["mode"] = new { type = "string" } } })
            }
        };

        return new Dictionary<string, object>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "RedCompute",
                ["description"] = "AI-native inference abstraction layer. Self-describing API for AI agents and human clients.",
                ["version"] = "0.1.0"
            },
            ["servers"] = new[] { new { url = $"http://localhost:{config.ApiPort}" } },
            ["paths"] = paths,
            ["components"] = new Dictionary<string, object>
            {
                ["schemas"] = new Dictionary<string, object>
                {
                    ["ErrorResponse"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["error"] = new { type = "string", description = "Machine-readable error code" },
                            ["message"] = new { type = "string", description = "Human-readable explanation" },
                            ["fields"] = new { type = "object", description = "Per-field validation errors (key=field, value=error)", additionalProperties = new { type = "string" } }
                        },
                        ["required"] = new[] { "error", "message" }
                    },
                    ["JobRecord"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["description"] = "A compute job tracked by RedCompute. Returned by GET /jobs (in items array) and GET /jobs/{id}.",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["id"] = new { type = "string", format = "uuid", description = "Unique job identifier" },
                            ["capability"] = Prop("string", "Capability slug (tts, stt, image-gen, music-gen, ai-session)"),
                            ["providerName"] = Prop("string", "Provider that executed/is executing this job"),
                            ["status"] = PropEnum("Job lifecycle state", "Queued", "Queued", "Running", "Completed", "Failed", "Cancelled"),
                            ["queuedAt"] = Prop("string", "ISO 8601 timestamp when the job was queued"),
                            ["startedAt"] = Prop("string", "ISO 8601 timestamp when execution began (null if still queued)"),
                            ["completedAt"] = Prop("string", "ISO 8601 timestamp when the job finished (null if not done)"),
                            ["durationMs"] = Prop("integer", "Wall-clock duration in milliseconds (null if not completed)"),
                            ["progress"] = PropNum("Execution progress", 0, 0, 1),
                            ["name"] = Prop("string", "User-friendly job name (null if auto-generated)"),
                            ["rationale"] = Prop("string", "Why this job was queued (set by the caller)"),
                            ["callerInfo"] = Prop("string", "Identifier of who queued the job (API caller, agent name, etc.)"),
                            ["errorMessage"] = Prop("string", "Error summary (only for Failed jobs)"),
                            ["errorDetails"] = Prop("string", "Full error details/stack trace (only on GET /jobs/{id})"),
                            ["input"] = Prop("string", "JSON-serialized input parameters (only on GET /jobs/{id})"),
                            ["outputLocation"] = Prop("string", "File path to output artifact (only on GET /jobs/{id})"),
                            ["outputSizeBytes"] = Prop("integer", "Output file size in bytes"),
                            ["outputContentType"] = Prop("string", "MIME type of the output (e.g. audio/wav, image/png)"),
                            ["resultMetadata"] = Prop("string", "JSON-serialized result metadata (only on GET /jobs/{id})")
                        }
                    },
                    ["ClaudeSessionInfo"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["id"] = Prop("string", "Short session identifier"),
                            ["projectName"] = Prop("string", "Project directory name"),
                            ["projectPath"] = Prop("string", "Full path to project"),
                            ["status"] = PropEnum("Session lifecycle state", "Idle", "Starting", "Active", "Idle", "Stopped", "Error"),
                            ["startedAt"] = Prop("string", "ISO 8601 timestamp"),
                            ["model"] = Prop("string", "Claude model identifier"),
                            ["claudeSessionId"] = Prop("string", "Internal Claude session UUID (for resume)"),
                            ["title"] = Prop("string", "Auto-generated conversation title"),
                            ["messageCount"] = Prop("integer", "Number of messages exchanged"),
                            ["costUsd"] = PropNum("Cost in USD", 0, 0, 999),
                            ["inputTokens"] = Prop("integer", "Input tokens consumed"),
                            ["outputTokens"] = Prop("integer", "Output tokens generated"),
                            ["cacheReadInputTokens"] = Prop("integer", "Tokens read from prompt cache"),
                            ["cacheCreationInputTokens"] = Prop("integer", "Tokens written to prompt cache"),
                            ["contextWindow"] = Prop("integer", "Max context window size for current model"),
                            ["effort"] = PropEnum("Reasoning effort level", "high", "low", "medium", "high", "xhigh", "max"),
                            ["permissionMode"] = PropEnum("Tool permission mode", "bypassPermissions", "bypassPermissions", "plan", "default", "acceptEdits", "auto")
                        }
                    },
                    ["ClaudeMessage"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["id"] = Prop("integer", "Auto-increment message ID"),
                            ["sessionId"] = Prop("string", "Parent session ID"),
                            ["role"] = PropEnum("Message author", "user", "user", "assistant"),
                            ["eventType"] = PropEnum("Content type", "text", "text", "thinking", "tool_use", "tool_result", "error", "status"),
                            ["content"] = Prop("string", "Text content"),
                            ["toolName"] = Prop("string", "Tool name (for tool_use events)"),
                            ["toolInput"] = Prop("string", "JSON-serialized tool input"),
                            ["toolResult"] = Prop("string", "Tool execution result"),
                            ["messageId"] = Prop("string", "Internal message ID for IPC linking"),
                            ["timestamp"] = Prop("string", "ISO 8601 timestamp")
                        }
                    },
                    ["ProjectInfo"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["name"] = Prop("string", "Project directory name"),
                            ["path"] = Prop("string", "Full filesystem path"),
                            ["hasClaudeMd"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Whether CLAUDE.md exists in the project" },
                            ["hasIcon"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Whether a project icon is available" }
                        }
                    },
                    ["ImageAttachment"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "mediaType", "base64" },
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["mediaType"] = PropEnum("MIME type", "image/png", "image/png", "image/jpeg", "image/gif", "image/webp"),
                            ["base64"] = Prop("string", "Base64-encoded image data")
                        }
                    }
                }
            }
        };
    }

    private static object Op(string id, string summary, string contentType, object schema) => new Dictionary<string, object>
    {
        ["operationId"] = id,
        ["summary"] = summary,
        ["responses"] = Responses(contentType, schema)
    };

    private static object Responses(string contentType, object schema) => new Dictionary<string, object>
    {
        ["200"] = new Dictionary<string, object>
        {
            ["description"] = "Success",
            ["content"] = new Dictionary<string, object>
            {
                [contentType] = new Dictionary<string, object> { ["schema"] = schema }
            }
        }
    };

    private static object ErrorResponse(string desc) => new Dictionary<string, object>
    {
        ["description"] = desc,
        ["content"] = new Dictionary<string, object>
        {
            ["application/json"] = new Dictionary<string, object>
            {
                ["schema"] = new { @ref = "#/components/schemas/ErrorResponse" }
            }
        }
    };

    private static object Schema(string type, string title) => new { type, title };

    private static object QueryParam(string name, string type, string desc) => new Dictionary<string, object>
    {
        ["name"] = name, ["in"] = "query", ["required"] = false,
        ["schema"] = new { type }, ["description"] = desc
    };

    private static object PathParam(string name, string type, string desc) => new Dictionary<string, object>
    {
        ["name"] = name, ["in"] = "path", ["required"] = true,
        ["schema"] = new { type }, ["description"] = desc
    };

    private static object RequestBody(object schema) => new Dictionary<string, object>
    {
        ["required"] = true,
        ["content"] = new Dictionary<string, object>
        {
            ["application/json"] = new Dictionary<string, object> { ["schema"] = schema }
        }
    };

    private static object Prop(string type, string desc, object? defaultVal = null)
    {
        var d = new Dictionary<string, object> { ["type"] = type, ["description"] = desc };
        if (defaultVal != null) d["default"] = defaultVal;
        return d;
    }

    private static object PropNum(string desc, double defaultVal, double min, double max) => new Dictionary<string, object>
    {
        ["type"] = "number", ["description"] = desc, ["default"] = defaultVal, ["minimum"] = min, ["maximum"] = max
    };

    private static object PropEnum(string desc, string defaultVal, params string[] values) => new Dictionary<string, object>
    {
        ["type"] = "string", ["description"] = desc, ["default"] = defaultVal, ["enum"] = values
    };

    private static Dictionary<string, object> BuildGenerateBody(CapabilityRegistry registry, string slug, Dictionary<string, object> properties, string[] required)
    {
        var entry = registry.Get(slug);
        if (entry != null && entry.Providers.Count > 1)
        {
            properties["provider"] = PropEnum(
                "Provider to use. Defaults to the configured default provider.",
                entry.DefaultProviderName ?? "",
                entry.Providers.Keys.ToArray());
        }

        var body = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["required"] = required,
            ["properties"] = properties
        };
        return body;
    }
}
