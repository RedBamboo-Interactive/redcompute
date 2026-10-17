using System.IO;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class GeneratedImagePayloadTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl0Y6sAAAAASUVORK5CYII=";

    [Fact]
    public void DecodesAndIdentifiesAValidPng()
    {
        var image = SessionTranscriptPipeline.DecodeImage(
        [
            new UnifiedAttachment { Type = "image", MimeType = "image/png", Data = OnePixelPng },
        ]);

        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(Convert.FromBase64String(OnePixelPng), image.Bytes);
    }

    [Fact]
    public void RejectsMalformedOrSpoofedImageData()
    {
        Assert.Throws<InvalidDataException>(() => SessionTranscriptPipeline.DecodeImage(
        [
            new UnifiedAttachment { Type = "image", MimeType = "image/png", Data = "not-base64" },
        ]));
        Assert.Throws<InvalidDataException>(() => SessionTranscriptPipeline.DecodeImage(
        [
            new UnifiedAttachment { Type = "image", MimeType = "image/jpeg", Data = OnePixelPng },
        ]));
    }

    [Fact]
    public void EncodedInputIsBoundedBeforeAllocation()
    {
        Assert.True(SessionTranscriptPipeline.IsImageBase64LengthWithinLimit(
            SessionTranscriptPipeline.MaxImagePayloadBase64Chars));
        Assert.False(SessionTranscriptPipeline.IsImageBase64LengthWithinLimit(
            SessionTranscriptPipeline.MaxImagePayloadBase64Chars + 1));
    }

    [Fact]
    public void BrowserEventKeepsOnlyTheLazyImageReference()
    {
        var payloadRef = new TranscriptPayloadRef
        {
            RecordId = 43,
            Kind = "assistant-image",
            ContentType = "image/png",
            Encoding = "binary",
        };
        var lightweight = SessionTranscriptPipeline.WithPayload(new UnifiedStreamEvent
        {
            Type = "image",
            Content = "A smoke-lilac portrait",
            Attachments =
            [
                new UnifiedAttachment { Type = "image", MimeType = "image/png", Data = OnePixelPng },
            ],
        }, payloadRef);

        Assert.Equal("A smoke-lilac portrait", lightweight.Content);
        Assert.Same(payloadRef, lightweight.PayloadRef);
        Assert.Null(lightweight.Attachments);
    }
}
