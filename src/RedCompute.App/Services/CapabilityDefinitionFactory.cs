using RedCompute.Core.Capabilities;

namespace RedCompute.App.Services;

public static class CapabilityDefinitionFactory
{
    public static CapabilityDefinition? Create(string slug)
    {
        return slug switch
        {
            "tts" => new CapabilityDefinition
            {
                Slug = "tts",
                Type = CapabilityType.Tts,
                DisplayName = "Text-to-Speech"
            },
            "stt" => new CapabilityDefinition
            {
                Slug = "stt",
                Type = CapabilityType.Stt,
                DisplayName = "Speech-to-Text"
            },
            "image-gen" => new CapabilityDefinition
            {
                Slug = "image-gen",
                Type = CapabilityType.ImageGen,
                DisplayName = "Image Generation"
            },
            "music-gen" => new CapabilityDefinition
            {
                Slug = "music-gen",
                Type = CapabilityType.MusicGen,
                DisplayName = "Music Generation"
            },
            "llm" => new CapabilityDefinition
            {
                Slug = "llm",
                Type = CapabilityType.Llm,
                DisplayName = "Language Model"
            },
            "ai-session" => new CapabilityDefinition
            {
                Slug = "ai-session",
                Type = CapabilityType.AiSession,
                DisplayName = "AI Session"
            },
            _ => null
        };
    }
}
