using RedCompute.Core.Sessions;

namespace RedCompute.App.Services.SessionArtifacts;

public static class ArtifactKinds
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Document = "document";
    public const string Link = "link";
    public const string Entity = "entity";

    public static readonly IReadOnlyList<string> All = [Image, Video, Audio, Document, Link, Entity];
}

public static class ArtifactOrigins
{
    public const string UserText = "user-text";
    public const string AssistantText = "assistant-text";
    public const string UserAttachment = "user-attachment";
    public const string AssistantImage = "assistant-image";
    public const string Voice = "voice";
    public const string ToolWrite = "tool-write";

    public static readonly IReadOnlyList<string> All =
        [UserText, AssistantText, UserAttachment, AssistantImage, Voice, ToolWrite];
}

public sealed class ArtifactEntityRef
{
    public required string TypeSlug { get; init; }
    public required string Id { get; init; }
}

public sealed class ArtifactFileRef
{
    public required string Path { get; init; }
    public int? Line { get; init; }
}

public sealed class ArtifactOccurrence
{
    public string? MessageUid { get; init; }
    public long RecordId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>One deduplicated thing a person saw in the conversation (read-only projection).</summary>
public sealed class SessionArtifact
{
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required string Origin { get; init; }
    public required string Role { get; init; }
    public required string Target { get; init; }
    public string? Title { get; init; }
    public string? MediaType { get; init; }
    public string? Name { get; init; }
    public long? Size { get; init; }
    public ArtifactEntityRef? Entity { get; init; }
    public ArtifactFileRef? File { get; init; }
    public TranscriptPayloadRef? PayloadRef { get; init; }
    public bool? Internal { get; init; }
    public required ArtifactOccurrence First { get; init; }
    public required ArtifactOccurrence Last { get; init; }
    public int Occurrences { get; init; }
}

/// <summary>An artifact found in one record, before deduplication.</summary>
public sealed record ArtifactCandidate(
    string Key,
    string Kind,
    string Origin,
    string Role,
    string Target,
    string? Title = null,
    string? MediaType = null,
    string? Name = null,
    long? Size = null,
    ArtifactEntityRef? Entity = null,
    ArtifactFileRef? File = null,
    TranscriptPayloadRef? PayloadRef = null,
    bool Internal = false);

public sealed class SessionArtifactPage
{
    public required string SessionId { get; init; }
    public required string Epoch { get; init; }
    public long ThroughSequence { get; init; }
    public int ExtractorVersion { get; init; } = SessionArtifactExtractor.Version;
    public required IReadOnlyList<SessionArtifact> Items { get; init; }
    public required SessionArtifactPageInfo Page { get; init; }
}

public sealed class SessionArtifactPageInfo
{
    public string? NextCursor { get; init; }
}
