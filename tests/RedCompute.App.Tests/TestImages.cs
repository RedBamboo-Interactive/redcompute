using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace RedCompute.App.Tests;

/// <summary>
/// Platform-neutral image fixtures for the generate image tests. PNG and JPEG come from SkiaSharp;
/// SkiaSharp cannot write GIF, so GIFs come from the minimal encoder below.
/// </summary>
internal static class TestImages
{
    public static readonly SKColor SteelBlue = new(70, 130, 180);

    public static byte[] Png(int width, int height, SKColor? color = null)
        => WithPixmap(Solid(width, height, color ?? SteelBlue), width, height,
            pixmap => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)));

    public static byte[] Jpeg(int width, int height, SKColor? color = null, int quality = 90)
        => Jpeg(Solid(width, height, color ?? SteelBlue), width, height, quality);

    public static byte[] Jpeg(byte[] bgraPixels, int width, int height, int quality)
        => WithPixmap(bgraPixels, width, height, pixmap => pixmap.Encode(
            new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)));

    /// <summary>A JPEG whose APP1 Exif segment carries the given orientation (tag 274).</summary>
    public static byte[] JpegWithOrientation(int width, int height, SKColor color, ushort orientation)
    {
        var jpeg = Jpeg(width, height, color);
        var app1 = new byte[36];
        app1[0] = 0xFF; app1[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(app1.AsSpan(2), 34);
        "Exif\0\0"u8.CopyTo(app1.AsSpan(4));
        var tiff = app1.AsSpan(10);
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[4..], 8);   // first IFD offset
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[8..], 1);   // one entry
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[10..], 274); // Orientation
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[12..], 3);  // SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[14..], 1);  // count
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[18..], orientation);
        // tiff[22..26]: next IFD offset 0
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    /// <summary>A GIF with one full-canvas solid frame per colour.</summary>
    public static byte[] Gif(int width, int height, params SKColor[] frames)
    {
        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        WriteUInt16(stream, width);
        WriteUInt16(stream, height);
        stream.Write([0x00, 0x00, 0x00]); // no global colour table
        foreach (var color in frames)
        {
            stream.Write([0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00]); // graphic control, 100 ms
            stream.WriteByte(0x2C);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, width);
            WriteUInt16(stream, height);
            stream.WriteByte(0x80); // local colour table of two entries
            stream.Write([color.Red, color.Green, color.Blue, 0, 0, 0]);
            WriteSolidLzw(stream, width * height);
        }
        stream.WriteByte(0x3B);
        return stream.ToArray();
    }

    /// <summary>Unpremultiplied BGRA of one pixel of an image, decoded with SkiaSharp.</summary>
    public static byte[] Pixel(byte[] image, int x, int y)
    {
        using var bitmap = Decode(image);
        var color = bitmap.GetPixel(x, y);
        return [color.Blue, color.Green, color.Red, color.Alpha];
    }

    public static int FrameCount(byte[] image)
    {
        using var codec = SKCodec.Create(new MemoryStream(image)) ?? throw new InvalidDataException("undecodable");
        return codec.FrameCount;
    }

    private static SKBitmap Decode(byte[] image)
    {
        using var codec = SKCodec.Create(new MemoryStream(image)) ?? throw new InvalidDataException("undecodable");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException(result.ToString());
        return bitmap;
    }

    private static byte[] Solid(int width, int height, SKColor color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.Blue; pixels[i + 1] = color.Green; pixels[i + 2] = color.Red; pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static byte[] WithPixmap(byte[] pixels, int width, int height, Func<SKPixmap, SKData?> encode)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(
                new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                handle.AddrOfPinnedObject(), width * 4);
            using var data = encode(pixmap) ?? throw new InvalidOperationException("encoding failed");
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// LZW image data for <paramref name="pixels"/> copies of palette index 0, minimum code size 2.
    /// A clear code before every two literals keeps the code width at three bits, so no dictionary
    /// bookkeeping is needed.
    /// </summary>
    private static void WriteSolidLzw(Stream stream, int pixels)
    {
        const int clear = 4, end = 5, width = 3;
        var bits = new List<bool>();
        void Emit(int code)
        {
            for (var i = 0; i < width; i++) bits.Add(((code >> i) & 1) != 0);
        }

        for (var i = 0; i < pixels; i++)
        {
            if (i % 2 == 0) Emit(clear);
            Emit(0);
        }
        Emit(end);

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8));

        stream.WriteByte(2);
        for (var offset = 0; offset < bytes.Length; offset += 255)
        {
            var size = Math.Min(255, bytes.Length - offset);
            stream.WriteByte((byte)size);
            stream.Write(bytes, offset, size);
        }
        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
