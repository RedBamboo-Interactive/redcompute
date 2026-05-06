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
                    ["summary"] = "Paginated job history, filterable by capability and status",
                    ["parameters"] = new object[]
                    {
                        QueryParam("capability", "string", "Filter by capability slug"),
                        QueryParam("status", "string", "Filter by job status (Queued, Running, Completed, Failed, Cancelled)"),
                        QueryParam("limit", "integer", "Max results (default 50)"),
                        QueryParam("offset", "integer", "Skip N results")
                    },
                    ["responses"] = Responses("application/json", Schema("array", "JobRecord"))
                }
            },
            ["/jobs/{id}"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["operationId"] = "GetJob",
                    ["summary"] = "Get full details of a single job",
                    ["parameters"] = new object[] { PathParam("id", "string", "Job UUID") },
                    ["responses"] = Responses("application/json", Schema("object", "JobRecord"))
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
            paths["/stt/transcribe"] = new Dictionary<string, object>
            {
                ["post"] = new Dictionary<string, object>
                {
                    ["operationId"] = "SttTranscribe",
                    ["summary"] = "Transcribe audio to text",
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
                                    ["properties"] = new Dictionary<string, object>
                                    {
                                        ["audio"] = new { type = "string", format = "binary", description = "Audio file (wav, mp3, opus, flac)" },
                                        ["language"] = Prop("string", "Language hint (ISO 639-1 or 'auto')", "auto")
                                    }
                                }
                            }
                        }
                    },
                    ["responses"] = Responses("application/json", new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["text"] = new { type = "string" },
                            ["language"] = new { type = "string" },
                            ["duration_ms"] = new { type = "integer" }
                        }
                    })
                }
            };
        }

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
