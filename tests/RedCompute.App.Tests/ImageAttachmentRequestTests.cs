using System.Text.Json;
using RedCompute.App.Api.Endpoints;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ImageAttachmentRequestTests
{
    [Fact]
    public void ExplicitNullImagesIsTreatedAsNoAttachment()
    {
        using var body = JsonDocument.Parse("""{"content":"hello","images":null}""");

        var hasImages = UnifiedSessionEndpoints.TryGetNonNullImages(body.RootElement, out _);

        Assert.False(hasImages);
    }

    [Fact]
    public void ImageArrayIsStillParsed()
    {
        using var body = JsonDocument.Parse("""{"content":"hello","images":[]}""");

        var hasImages = UnifiedSessionEndpoints.TryGetNonNullImages(body.RootElement, out var images);

        Assert.True(hasImages);
        Assert.Equal(JsonValueKind.Array, images.ValueKind);
    }
}
