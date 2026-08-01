namespace RedCompute.Core.Sessions;

/// <summary>A provider-neutral user input part. HTTP callers send only text and attachment IDs;
/// the endpoint resolves IDs to immutable attachment metadata before providers see them.</summary>
public sealed class SessionInputPart
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public InputAttachment? Attachment { get; init; }
    public ImageAttachment? LegacyImage { get; init; }

    public static SessionInputPart TextPart(string text) => new() { Type = "text", Text = text };
    public static SessionInputPart AttachmentPart(InputAttachment attachment) => new() { Type = "attachment", Attachment = attachment };
    public static SessionInputPart LegacyImagePart(ImageAttachment image) => new() { Type = "legacy-image", LegacyImage = image };
}

/// <summary>Immutable metadata for bytes owned by RedCompute's staged attachment store.</summary>
public sealed class InputAttachment
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string MediaType { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required string StoredPath { get; init; }
    public required string DownloadUrl { get; init; }
}

public static class SessionInputFormatting
{
    public static string UserText(IReadOnlyList<SessionInputPart> input) =>
        string.Join("\n", input.Where(p => p.Type == "text" && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text));

    public static string FileReference(InputAttachment attachment) =>
        $"Attached file \"{attachment.Name}\" ({attachment.MediaType}, {attachment.Size} bytes, SHA-256 {attachment.Sha256}) is available at this read-only local path: \"{attachment.StoredPath}\".";
}
