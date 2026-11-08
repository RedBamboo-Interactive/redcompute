using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RedCompute.Core.Sessions;
using SkiaSharp;

namespace RedCompute.App.Services;

/// <summary>
/// Decodes and validates one generate image before any provider sees it. Images are untrusted
/// input (inbound user images can carry injected text, and the bytes themselves can be hostile), so
/// every image must pass, in order:
///
/// <list type="number">
/// <item>a byte-size cap, checked before base64 decoding or reading a staged file;</item>
/// <item>a format sniff that must agree with the declared media type;</item>
/// <item>a structural walk of the container (PNG chunk CRCs through IEND, JPEG markers ending in EOI,
/// every GIF block through the trailer, WebP RIFF size), because platform decoders tolerate
/// truncated data and accept GIF frames that disagree with the canvas;</item>
/// <item>dimension caps on the canvas and on every GIF frame, read from the headers, so a
/// decompression bomb is refused before any pixel buffer is allocated;</item>
/// <item>a full Skia decode of the first frame whose canvas and frame rectangle must match the
/// headers, and which must complete without error.</item>
/// </list>
///
/// Nothing the caller sent is forwarded. Every image is re-encoded as a fresh PNG from its decoded
/// pixels (after applying any EXIF orientation), which drops EXIF/GPS, text chunks, APPn/COM/XMP
/// segments, polyglot payloads and anything after the image data. A GIF or animated PNG contributes
/// its first frame, composited onto its canvas. If the PNG exceeds <see cref="MaxImageBytes"/> it
/// is downscaled until it fits. Animated WebP is refused.
///
/// Decoding runs in-process through SkiaSharp (libpng, libjpeg-turbo, libwebp and Skia's GIF
/// decoder), identically on every platform. Moving decoding into an isolated helper process is a
/// candidate follow-up; see docs/tool-free-generate.md.
/// </summary>
public static class GenerateImageValidator
{
    public const int MaxImages = 4;
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const int MaxDimension = 4096;
    public const long MaxPixels = 4096L * 4096L;

    public static readonly IReadOnlyList<string> AllowedMediaTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    /// <summary>Upper bound on GIF blocks (extensions and frames) walked before giving up.</summary>
    public const int MaxGifBlocks = 4096;

    // One decode at a time bounds transient pixel memory to one image.
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);

    /// <summary>Longest base64 text that can decode to at most <see cref="MaxImageBytes"/>.</summary>
    public static int MaxBase64Length => (MaxImageBytes + 2) / 3 * 4;

    public static async Task<(GenerateImage? Image, string? Error)> ValidateAsync(
        byte[] bytes, string? declaredMediaType, CancellationToken ct)
    {
        if (bytes.Length == 0) return (null, "image is empty");
        if (bytes.Length > MaxImageBytes) return (null, $"image exceeds {MaxImageBytes} bytes");

        var format = Sniff(bytes);
        if (format == null)
            return (null, "image is not PNG, JPEG, WebP or GIF");
        if (declaredMediaType != null && !string.Equals(Normalize(declaredMediaType), format, StringComparison.Ordinal))
            return (null, $"declared media type '{declaredMediaType}' does not match the image data ({format})");

        var header = format switch
        {
            "image/png" => ReadPng(bytes),
            "image/jpeg" => ReadJpeg(bytes),
            "image/gif" => ReadGif(bytes),
            _ => ReadWebp(bytes),
        };
        if (header.Error != null) return (null, header.Error);
        if (header.Width <= 0 || header.Height <= 0) return (null, "image has no dimensions");
        if (header.Width > MaxDimension || header.Height > MaxDimension)
            return (null, $"image is {header.Width}x{header.Height}; each side must be at most {MaxDimension} pixels");
        if ((long)header.Width * header.Height > MaxPixels)
            return (null, $"image has more than {MaxPixels} pixels");

        await DecodeGate.WaitAsync(ct);
        try
        {
            return Decode(bytes, header);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>
    /// Decodes the first frame and re-encodes it as PNG. Internal so tests can drive the decoder
    /// with bytes the structural walk would refuse; any non-fatal failure is a refusal, never an
    /// exception.
    /// </summary>
    internal static (GenerateImage? Image, string? Error) Decode(byte[] bytes, Header header)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var codec = SKCodec.Create(stream, out var created);
            if (codec is null) return (null, $"image could not be decoded ({created})");

            // Skia composites the first GIF frame onto its canvas itself, so the canvas must
            // match the header canvas and the frame rectangle the header's first-frame region.
            var region = header.FirstFrame;
            var canvas = codec.Info;
            if (canvas.Width != header.Width || canvas.Height != header.Height)
                return (null, $"decoded size {canvas.Width}x{canvas.Height} does not match the header {header.Width}x{header.Height}");
            if (codec.EncodedFormat == SKEncodedImageFormat.Gif)
            {
                // FrameCount parses the frame headers; frame info is unavailable before it.
                if (codec.FrameCount < 1 || !codec.GetFrameInfo(0, out var frameInfo))
                    return (null, "image has no frames");
                var rect = frameInfo.FrameRect;
                if (rect.Left != region.Left || rect.Top != region.Top || rect.Width != region.Width || rect.Height != region.Height)
                    return (null, $"decoded size {rect.Width}x{rect.Height} does not match the header {region.Width}x{region.Height}");
            }

            // Decode every pixel row into a fixed, unpremultiplied 32-bit BGRA layout with no
            // colour management; a corrupt or incomplete stream is refused here. The buffer
            // starts zeroed, so canvas outside a smaller GIF frame stays transparent.
            var stride = checked(header.Width * 4);
            var pixels = new byte[checked(stride * header.Height)];
            var target = new SKImageInfo(header.Width, header.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            SKCodecResult decoded;
            try
            {
                decoded = codec.GetPixels(target, handle.AddrOfPinnedObject(), stride, new SKCodecOptions(0));
            }
            finally
            {
                handle.Free();
            }
            if (decoded != SKCodecResult.Success)
                return (null, $"image could not be decoded ({decoded})");

            var image = Orient(new Bgra(header.Width, header.Height, pixels), (int)codec.EncodedOrigin);
            return EncodeWithinLimit(image, bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hostile input can surface as many exception types (arithmetic overflow, native
            // allocation failure, ...). All are refusals.
            return (null, $"image could not be decoded ({ex.GetType().Name})");
        }
    }

    /// <summary>Unpremultiplied BGRA pixels, tightly packed (stride = width * 4).</summary>
    internal sealed record Bgra(int Width, int Height, byte[] Pixels);

    /// <summary>
    /// Fresh PNG from decoded pixels only, so no metadata or trailing data from the source survives.
    /// Downscales (at most four times) until the PNG fits <see cref="MaxImageBytes"/>.
    /// </summary>
    private static (GenerateImage? Image, string? Error) EncodeWithinLimit(Bgra image, byte[] source)
    {
        var scale = 1.0;
        var candidate = image;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var png = EncodePng(candidate);
            if (png.Length <= MaxImageBytes)
                return (new GenerateImage("image/png", png, candidate.Width, candidate.Height,
                    Sha256(png), Sha256(source)), null);
            scale *= Math.Sqrt((double)MaxImageBytes / png.Length) * 0.9;
            candidate = Scale(image, scale);
        }
        return (null, $"image does not fit {MaxImageBytes} bytes as PNG even after downscaling");
    }

    private static byte[] EncodePng(Bgra image)
    {
        var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(Info(image), handle.AddrOfPinnedObject(), image.Width * 4);
            using var png = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6))
                            ?? throw new InvalidOperationException("PNG encoding failed");
            return png.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    private static Bgra Scale(Bgra image, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var scaled = new Bgra(width, height, new byte[checked(width * height * 4)]);
        var source = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        var target = GCHandle.Alloc(scaled.Pixels, GCHandleType.Pinned);
        try
        {
            using var from = new SKPixmap(Info(image), source.AddrOfPinnedObject(), image.Width * 4);
            using var to = new SKPixmap(Info(scaled), target.AddrOfPinnedObject(), width * 4);
            if (!from.ScalePixels(to, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
                throw new InvalidOperationException("downscaling failed");
            return scaled;
        }
        finally
        {
            source.Free();
            target.Free();
        }
    }

    private static SKImageInfo Info(Bgra image) =>
        new(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

    /// <summary>
    /// Applies an EXIF orientation (1-8, as Skia reports it; anything else is upright) so dropping
    /// the metadata does not turn the image sideways. Pixels are moved exactly, never resampled.
    /// </summary>
    internal static Bgra Orient(Bgra image, int orientation)
    {
        if (orientation is < 2 or > 8) return image;
        int w = image.Width, h = image.Height;
        var ow = orientation >= 5 ? h : w;
        var oh = orientation >= 5 ? w : h;
        var source = MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan());
        var result = new byte[image.Pixels.Length];
        var target = MemoryMarshal.Cast<byte, uint>(result.AsSpan());
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (tx, ty) = orientation switch
            {
                2 => (w - 1 - x, y),          // mirror horizontally
                3 => (w - 1 - x, h - 1 - y),  // rotate 180
                4 => (x, h - 1 - y),          // mirror vertically
                5 => (y, x),                  // transpose
                6 => (h - 1 - y, x),          // rotate 90 clockwise
                7 => (h - 1 - y, w - 1 - x),  // transverse
                _ => (y, w - 1 - x),          // 8: rotate 270 clockwise
            };
            target[ty * ow + tx] = source[y * w + x];
        }
        return new Bgra(ow, oh, result);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Normalize(string mediaType)
    {
        var value = mediaType.Split(';')[0].Trim().ToLowerInvariant();
        return value is "image/jpg" or "image/pjpeg" ? "image/jpeg" : value;
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    internal static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[..8].SequenceEqual(PngSignature)) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    internal readonly record struct Region(int Left, int Top, int Width, int Height);

    /// <summary>Canvas size and the first frame's placement on it (the whole canvas except for GIF).</summary>
    internal readonly record struct Header(int Width, int Height, Region FirstFrame, string? Error)
    {
        public Header(int width, int height) : this(width, height, new Region(0, 0, width, height), null) { }

        public static Header Fail(string error) => new(0, 0, default, error);
    }

    // --- PNG: every chunk length-bounded and CRC-checked, IHDR first, IDAT present, IEND last ---

    private static Header ReadPng(byte[] b)
    {
        var offset = 8;
        int width = 0, height = 0;
        bool sawIdat = false, first = true;
        while (true)
        {
            if (offset + 12 > b.Length) return Header.Fail("PNG is truncated (no IEND chunk)");
            var length = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset));
            if (length > (uint)(b.Length - offset - 12)) return Header.Fail("PNG is truncated (a chunk overruns the data)");
            var type = b.AsSpan(offset + 4, 4);
            var data = b.AsSpan(offset + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset + 8 + (int)length));
            if (Crc32(b.AsSpan(offset + 4, 4 + (int)length)) != crc) return Header.Fail("PNG chunk CRC mismatch");

            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return Header.Fail("PNG does not start with IHDR");
                width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data));
                height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                first = false;
            }
            else if (type.SequenceEqual("IDAT"u8)) sawIdat = true;
            else if (type.SequenceEqual("IEND"u8))
            {
                offset += 12 + (int)length;
                if (offset != b.Length) return Header.Fail("PNG has data after IEND");
                return sawIdat ? new Header(width, height) : Header.Fail("PNG has no image data");
            }
            offset += 12 + (int)length;
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in data) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // --- JPEG: marker segments to a frame header and a scan, data ending in EOI ---

    private static Header ReadJpeg(byte[] b)
    {
        if (b.Length < 4 || b[^2] != 0xFF || b[^1] != 0xD9) return Header.Fail("JPEG is truncated (no end-of-image marker)");
        var offset = 2;
        int width = 0, height = 0;
        while (offset + 4 <= b.Length)
        {
            if (b[offset] != 0xFF) return Header.Fail("JPEG marker structure is invalid");
            var marker = b[offset + 1];
            if (marker == 0xFF) { offset++; continue; }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { offset += 2; continue; }
            if (marker == 0xD9) return Header.Fail("JPEG ends before any scan");
            var length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset + 2));
            if (length < 2 || offset + 2 + length > b.Length) return Header.Fail("JPEG segment overruns the data");
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 7) return Header.Fail("JPEG frame header is too short");
                height = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset + 5));
                width = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset + 7));
            }
            if (marker == 0xDA)
                return width > 0 && height > 0
                    ? new Header(width, height)
                    : Header.Fail("JPEG scan precedes its frame header");
            offset += 2 + length;
        }
        return Header.Fail("JPEG has no scan");
    }

    // --- GIF: every block walked to the trailer; every frame bounded and inside the canvas ---

    internal static Header ReadGif(byte[] b)
    {
        if (b.Length < 14) return Header.Fail("GIF is truncated");
        var canvasWidth = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));
        var canvasHeight = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
        var offset = 13 + ColorTableSize(b[10]);
        Region? first = null;
        for (var blocks = 0; ; blocks++)
        {
            if (blocks >= MaxGifBlocks) return Header.Fail($"GIF has more than {MaxGifBlocks} blocks");
            if (offset >= b.Length) return Header.Fail("GIF is truncated (no trailer)");
            switch (b[offset])
            {
                case 0x3B:
                    if (offset != b.Length - 1) return Header.Fail("GIF has data after its trailer");
                    return first is { } frame
                        ? new Header(canvasWidth, canvasHeight, frame, null)
                        : Header.Fail("GIF has no image");
                case 0x21:
                    offset += 2;
                    if (!SkipSubBlocks(b, ref offset)) return Header.Fail("GIF is truncated (extension)");
                    break;
                case 0x2C:
                    if (offset + 10 > b.Length) return Header.Fail("GIF is truncated (image descriptor)");
                    var region = new Region(
                        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset + 1)),
                        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset + 3)),
                        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset + 5)),
                        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset + 7)));
                    if (region.Width == 0 || region.Height == 0)
                        return Header.Fail("GIF frame has no dimensions");
                    if (region.Width > MaxDimension || region.Height > MaxDimension)
                        return Header.Fail($"GIF frame is {region.Width}x{region.Height}; each side must be at most {MaxDimension} pixels");
                    if (region.Left + region.Width > canvasWidth || region.Top + region.Height > canvasHeight)
                        return Header.Fail($"GIF frame {region.Width}x{region.Height} at {region.Left},{region.Top} lies outside the {canvasWidth}x{canvasHeight} canvas");
                    first ??= region;
                    offset += 10 + ColorTableSize(b[offset + 9]);
                    if (offset >= b.Length) return Header.Fail("GIF is truncated (image data)");
                    if (b[offset] is < 2 or > 11) return Header.Fail("GIF image has an invalid LZW code size");
                    offset++;
                    if (!SkipSubBlocks(b, ref offset)) return Header.Fail("GIF is truncated (image data)");
                    break;
                default:
                    return Header.Fail("GIF block structure is invalid");
            }
        }
    }

    private static int ColorTableSize(byte packed) => (packed & 0x80) != 0 ? 3 << ((packed & 0x07) + 1) : 0;

    /// <summary>Skips data sub-blocks through the zero terminator; each step consumes at least one byte.</summary>
    private static bool SkipSubBlocks(byte[] b, ref int offset)
    {
        while (true)
        {
            if (offset >= b.Length) return false;
            var size = b[offset++];
            if (size == 0) return true;
            offset += size;
            if (offset > b.Length) return false;
        }
    }

    // --- WebP: RIFF size covers the file exactly; VP8, VP8L or VP8X canvas size ---

    private static Header ReadWebp(byte[] b)
    {
        if (b.Length < 30) return Header.Fail("WebP is truncated");
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        if ((long)riffSize + 8 != b.Length) return Header.Fail("WebP RIFF size does not match the data");
        var chunk = b.AsSpan(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            if ((b[20] & 0x02) != 0) return Header.Fail("animated WebP is not supported; send a still image or GIF");
            var width = 1 + (b[24] | b[25] << 8 | b[26] << 16);
            var height = 1 + (b[27] | b[28] << 8 | b[29] << 16);
            return new Header(width, height);
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (b[20] != 0x2F) return Header.Fail("WebP lossless signature is invalid");
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21));
            return new Header((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return Header.Fail("WebP lossy start code is invalid");
            return new Header(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3FFF);
        }
        return Header.Fail("WebP has no image chunk");
    }
}
