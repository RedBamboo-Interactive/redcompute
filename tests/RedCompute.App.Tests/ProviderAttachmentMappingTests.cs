using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.OpenCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ProviderAttachmentMappingTests : IDisposable
{
    private readonly string _imagePath = Path.Combine(Path.GetTempPath(), $"redcompute-provider-image-{Guid.NewGuid():N}.bin");

    public ProviderAttachmentMappingTests() => File.WriteAllBytes(_imagePath, "image-bytes"u8.ToArray());

    [Fact]
    public void ClaudeRetainsNativeImagesAndLowersFilesToPathReferences()
    {
        var payload = ClaudeSessionService.BuildContentPayload(Input());
        var json = JsonSerializer.SerializeToElement(payload);

        Assert.Equal("text", json[0].GetProperty("type").GetString());
        Assert.Equal("image", json[1].GetProperty("type").GetString());
        Assert.Equal("base64", json[1].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("text", json[2].GetProperty("type").GetString());
        Assert.Contains("notes.txt", json[2].GetProperty("text").GetString());
        Assert.Contains(@"C:\attachments\notes.bin", json[2].GetProperty("text").GetString());
    }

    [Fact]
    public void OpenCodeRetainsNativeImagesAndUsesVerifiedFallbackForFiles()
    {
        var payload = OpenCodeSessionService.BuildPromptBlocks(Input());
        var json = JsonSerializer.SerializeToElement(payload);

        Assert.Equal("image", json[1].GetProperty("type").GetString());
        Assert.Equal("image/png", json[1].GetProperty("mimeType").GetString());
        Assert.Equal("text", json[2].GetProperty("type").GetString());
        Assert.Contains("read-only local path", json[2].GetProperty("text").GetString());
        Assert.DoesNotContain("resource", json[2].GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private SessionInputPart[] Input() =>
    [
        SessionInputPart.TextPart("Review"),
        SessionInputPart.AttachmentPart(new InputAttachment
        {
            Id = "att_image", Kind = "image", Name = "image.png", MediaType = "image/png",
            Size = 11, Sha256 = "abc", StoredPath = _imagePath, DownloadUrl = "/image",
        }),
        SessionInputPart.AttachmentPart(new InputAttachment
        {
            Id = "att_file", Kind = "file", Name = "notes.txt", MediaType = "text/plain",
            Size = 12, Sha256 = "def", StoredPath = @"C:\attachments\notes.bin", DownloadUrl = "/file",
        }),
    ];

    public void Dispose()
    {
        if (File.Exists(_imagePath)) File.Delete(_imagePath);
    }
}
