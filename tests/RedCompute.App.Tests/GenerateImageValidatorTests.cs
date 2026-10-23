using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class GenerateImageValidatorTests
{
    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "vision", name);

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public async Task Still_png_and_jpeg_are_forwarded_byte_for_byte(string mediaType)
    {
        var bytes = Encode(mediaType == "image/png" ? new PngBitmapEncoder() : new JpegBitmapEncoder(), 64, 32);

        var (image, error) = await GenerateImageValidator.ValidateAsync(bytes, mediaType, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(mediaType, image!.MediaType);
        Assert.Equal(bytes, image.Bytes);
        Assert.Equal((64, 32), (image.Width, image.Height));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), image.Sha256);
    }

    [Theory]
    [InlineData("still.webp", 64, 32)]
    [InlineData("still-lossy.webp", 64, 32)]
    [InlineData("vision-orchid.png", 480, 160)]
    public async Task Fixture_images_validate(string name, int width, int height)
    {
        var bytes = await File.ReadAllBytesAsync(FixturePath(name));

        var (image, error) = await GenerateImageValidator.ValidateAsync(bytes, null, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(bytes, image!.Bytes);
        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Fact]
    public async Task An_animated_gif_is_reduced_to_its_first_frame_as_png()
    {
        var encoder = new GifBitmapEncoder();
        foreach (var color in new[] { Colors.Red, Colors.Lime, Colors.Blue })
            encoder.Frames.Add(BitmapFrame.Create(Solid(40, 20, color)));
        var bytes = Save(encoder);

        var (image, error) = await GenerateImageValidator.ValidateAsync(bytes, "image/gif", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("image/png", image!.MediaType);
        Assert.Equal("image/png", GenerateImageValidator.Sniff(image.Bytes));
        var decoded = BitmapDecoder.Create(new MemoryStream(image.Bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Single(decoded.Frames);
        var pixel = new byte[4];
        new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Bgra32, null, 0)
            .CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.Equal((byte)255, pixel[2]); // red: the first frame
        Assert.Equal((byte)0, pixel[1]);
    }

    [Fact]
    public async Task An_animated_png_is_reduced_to_its_default_image()
    {
        var png = Encode(new PngBitmapEncoder(), 16, 16);
        var actl = Chunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]);
        var animated = png[..33].Concat(actl).Concat(png[33..]).ToArray();

        var (image, error) = await GenerateImageValidator.ValidateAsync(animated, "image/png", CancellationToken.None);

        Assert.Null(error);
        Assert.NotEqual(animated, image!.Bytes);
        Assert.DoesNotContain("acTL", System.Text.Encoding.ASCII.GetString(image.Bytes));
    }

    [Fact]
    public async Task Animated_webp_is_refused()
    {
        var (image, error) = await GenerateImageValidator.ValidateAsync(
            await File.ReadAllBytesAsync(FixturePath("animated.webp")), "image/webp", CancellationToken.None);

        Assert.Null(image);
        Assert.Contains("animated WebP", error);
    }

    [Fact]
    public async Task Truncated_images_are_refused_although_the_platform_decoder_would_accept_them()
    {
        var png = Encode(new PngBitmapEncoder(), 64, 64);
        var jpeg = Encode(new JpegBitmapEncoder(), 64, 64);
        var webp = await File.ReadAllBytesAsync(FixturePath("still-lossy.webp"));

        Assert.Contains("truncated", (await Validate(png[..(png.Length / 2)], "image/png")));
        Assert.Contains("truncated", (await Validate(jpeg[..(jpeg.Length / 2)], "image/jpeg")));
        Assert.Contains("RIFF size", (await Validate(webp[..^4], "image/webp")));
    }

    [Fact]
    public async Task Structural_corruption_is_refused()
    {
        var png = Encode(new PngBitmapEncoder(), 8, 8);
        var flipped = (byte[])png.Clone();
        flipped[40] ^= 0xFF;
        Assert.Contains("CRC", await Validate(flipped, "image/png"));
        Assert.Contains("after IEND", await Validate([.. png, 1, 2, 3], "image/png"));
        Assert.Contains("not PNG, JPEG, WebP or GIF", await Validate("GIF8 not really"u8.ToArray(), null));
        Assert.Contains("not PNG, JPEG, WebP or GIF", await Validate(new byte[100], null));
        Assert.Contains("does not start with IHDR", await Validate([.. png[..8], .. Chunk("IEND", [])], "image/png"));
    }

    [Theory]
    [InlineData(50_000, 50_000)]
    [InlineData(4097, 16)]
    [InlineData(16, 4097)]
    public async Task Bombs_are_refused_from_the_header_before_any_decode(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6;
        // A few bytes of zlib that would inflate to gigabytes if anything tried.
        byte[] bomb = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", ihdr), .. Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]), .. Chunk("IEND", [])];

        var error = await Validate(bomb, "image/png");

        Assert.Contains($"{width}x{height}", error);
    }

    [Fact]
    public async Task Jpeg_and_gif_headers_are_bounded_too()
    {
        var jpeg = Encode(new JpegBitmapEncoder(), 32, 32);
        var sof = Enumerable.Range(0, jpeg.Length - 1).First(i => jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 5), 60000);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 7), 60000);
        Assert.Contains("60000x60000", await Validate(jpeg, "image/jpeg"));

        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Solid(8, 8, Colors.Red)));
        var gif = Save(encoder);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 9000);
        Assert.Contains("9000x8", await Validate(gif, "image/gif"));
    }

    [Fact]
    public async Task The_largest_allowed_image_decodes()
    {
        var bytes = Encode(new PngBitmapEncoder(), GenerateImageValidator.MaxDimension, GenerateImageValidator.MaxDimension);

        var (image, error) = await GenerateImageValidator.ValidateAsync(bytes, "image/png", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(GenerateImageValidator.MaxDimension, image!.Width);
    }

    [Fact]
    public async Task Byte_size_and_declared_type_are_enforced()
    {
        Assert.Contains("exceeds", await Validate(new byte[GenerateImageValidator.MaxImageBytes + 1], "image/png"));
        Assert.Contains("empty", await Validate([], "image/png"));
        Assert.Contains("does not match", await Validate(Encode(new PngBitmapEncoder(), 4, 4), "image/jpeg"));
        Assert.Null(await Validate(Encode(new JpegBitmapEncoder(), 4, 4), "image/jpg"));
    }

    // --- helpers ---

    private static async Task<string?> Validate(byte[] bytes, string? mediaType)
        => (await GenerateImageValidator.ValidateAsync(bytes, mediaType, CancellationToken.None)).Error;

    internal static byte[] Encode(BitmapEncoder encoder, int width, int height)
    {
        encoder.Frames.Add(BitmapFrame.Create(Solid(width, height, Colors.SteelBlue)));
        return Save(encoder);
    }

    private static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static byte[] Save(BitmapEncoder encoder)
    {
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in data)
        {
            c ^= x;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}
