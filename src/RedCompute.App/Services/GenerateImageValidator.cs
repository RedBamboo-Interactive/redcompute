using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RedCompute.Core.Sessions;

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
/// GIF trailer, WebP RIFF size), because WIC silently fills in truncated PNG and JPEG data;</item>
/// <item>dimension caps read from the header, so a decompression bomb is refused before any pixel
/// buffer is allocated;</item>
/// <item>a full WIC decode of the first frame whose dimensions must match the header.</item>
/// </list>
///
/// PNG, JPEG and still WebP bytes are forwarded unchanged. A GIF or an animated PNG is reduced to its
/// first frame, re-encoded as PNG. Animated WebP is refused (WIC on Windows cannot decode it).
/// </summary>
public static class GenerateImageValidator
{
    public const int MaxImages = 4;
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const int MaxDimension = 4096;
    public const long MaxPixels = 4096L * 4096L;

    public static readonly IReadOnlyList<string> AllowedMediaTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    // One decode at a time bounds transient pixel memory to one image (at most MaxPixels at 8 bytes).
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
            return Decode(bytes, format, header);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static (GenerateImage? Image, string? Error) Decode(byte[] bytes, string format, Header header)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return (null, "image has no frames");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != header.Width || frame.PixelHeight != header.Height)
                return (null, $"decoded size {frame.PixelWidth}x{frame.PixelHeight} does not match the header {header.Width}x{header.Height}");

            // Force a full decode of every pixel row into a fixed 32-bit layout (at most
            // MaxPixels * 4 bytes); a corrupt stream throws here.
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = checked(converted.PixelWidth * 4);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);

            if (format == "image/gif" || header.Animated)
            {
                var encoder = new PngBitmapEncoder();
                // From the decoded pixels, not the source frame: the source frame's metadata
                // (acTL, text chunks, comments) would otherwise be copied into the new PNG.
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
                    96, 96, PixelFormats.Bgra32, null, pixels, stride)));
                using var png = new MemoryStream();
                encoder.Save(png);
                return (Image("image/png", png.ToArray(), frame.PixelWidth, frame.PixelHeight), null);
            }

            return (Image(format, bytes, frame.PixelWidth, frame.PixelHeight), null);
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or ArgumentException
                                       or IOException or COMException or OverflowException
                                       or InvalidOperationException or OutOfMemoryException)
        {
            return (null, $"image could not be decoded ({ex.GetType().Name})");
        }
    }

    private static GenerateImage Image(string mediaType, byte[] bytes, int width, int height)
        => new(mediaType, bytes, width, height, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

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

    private readonly record struct Header(int Width, int Height, bool Animated, string? Error)
    {
        public static Header Fail(string error) => new(0, 0, false, error);
    }

    // --- PNG: every chunk length-bounded and CRC-checked, IHDR first, IDAT present, IEND last ---

    private static Header ReadPng(byte[] b)
    {
        var offset = 8;
        int width = 0, height = 0;
        bool sawIdat = false, animated = false, first = true;
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
            else if (type.SequenceEqual("acTL"u8)) animated = true;
            else if (type.SequenceEqual("IEND"u8))
            {
                offset += 12 + (int)length;
                if (offset != b.Length) return Header.Fail("PNG has data after IEND");
                return sawIdat ? new Header(width, height, animated, null) : Header.Fail("PNG has no image data");
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
                    ? new Header(width, height, false, null)
                    : Header.Fail("JPEG scan precedes its frame header");
            offset += 2 + length;
        }
        return Header.Fail("JPEG has no scan");
    }

    // --- GIF: logical screen size and the trailer byte ---

    private static Header ReadGif(byte[] b)
    {
        if (b.Length < 14) return Header.Fail("GIF is truncated");
        if (b[^1] != 0x3B) return Header.Fail("GIF is truncated (no trailer)");
        return new Header(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)),
            BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)), false, null);
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
            return new Header(width, height, false, null);
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (b[20] != 0x2F) return Header.Fail("WebP lossless signature is invalid");
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21));
            return new Header((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, false, null);
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return Header.Fail("WebP lossy start code is invalid");
            return new Header(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3FFF, false, null);
        }
        return Header.Fail("WebP has no image chunk");
    }
}
