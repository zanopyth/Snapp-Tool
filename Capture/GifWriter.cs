using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;

namespace SnapTool.Capture;

/// <summary>
/// Writes a multi-frame animated GIF from a sequence of bitmaps, with no external dependencies.
/// .NET's own GIF encoder (via Bitmap.Save) already does per-image color quantization and LZW
/// compression correctly — it just only ever writes a single-frame file. So instead of re-implementing
/// a quantizer/compressor, each frame is saved individually through that encoder and its already-encoded
/// color table + compressed image data are spliced into a hand-built GIF89a container (Netscape loop
/// extension + a Graphic Control Extension per frame for timing).
/// </summary>
internal static class GifWriter
{
    public static void SaveAnimated(string path, IReadOnlyList<Bitmap> frames, int delayMs)
    {
        if (frames.Count == 0) return;
        int width = frames[0].Width;
        int height = frames[0].Height;
        int delayCs = Math.Clamp(delayMs / 10, 1, ushort.MaxValue);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteHeaderAndLoop(fs, width, height);

        foreach (var frame in frames)
        {
            var (packed, colorTable, imageData) = ExtractSingleFrame(frame);
            WriteGraphicControlExtension(fs, delayCs);
            WriteImageDescriptorAndData(fs, width, height, packed, colorTable, imageData);
        }

        fs.WriteByte(0x3B); // trailer
    }

    private static (byte packed, byte[] colorTable, byte[] imageData) ExtractSingleFrame(Bitmap frame)
    {
        using var ms = new MemoryStream();
        frame.Save(ms, ImageFormat.Gif);
        return ParseSingleFrameGif(ms.ToArray());
    }

    /// <summary>Pulls the color table and already-LZW-compressed image data out of a single-frame
    /// GIF produced by GDI+, so they can be reused as-is in the combined animation.</summary>
    private static (byte packed, byte[] colorTable, byte[] imageData) ParseSingleFrameGif(byte[] g)
    {
        int pos = 6; // "GIF87a" / "GIF89a"
        byte lsdPacked = g[pos + 4];
        pos += 7;

        byte[]? globalColorTable = null;
        if ((lsdPacked & 0x80) != 0)
        {
            int gctBytes = (2 << (lsdPacked & 0x07)) * 3;
            globalColorTable = g[pos..(pos + gctBytes)];
            pos += gctBytes;
        }

        // Skip extension blocks (e.g. a Graphic Control Extension GDI+ may add) until the Image Descriptor.
        while (g[pos] == 0x21)
        {
            pos += 2;
            while (true)
            {
                byte n = g[pos];
                pos += 1;
                if (n == 0) break;
                pos += n;
            }
        }

        pos += 1 + 8; // image separator (0x2C) + left/top/width/height
        byte idPacked = g[pos];
        pos += 1;

        byte[] colorTable;
        if ((idPacked & 0x80) != 0)
        {
            int lctBytes = (2 << (idPacked & 0x07)) * 3;
            colorTable = g[pos..(pos + lctBytes)];
            pos += lctBytes;
        }
        else
        {
            colorTable = globalColorTable ?? throw new InvalidDataException("GIF frame has no color table.");
        }

        int dataStart = pos;
        pos += 1; // LZW minimum code size
        while (true)
        {
            byte n = g[pos];
            pos += 1;
            if (n == 0) break;
            pos += n;
        }
        byte[] imageData = g[dataStart..pos];

        int colorCount = colorTable.Length / 3;
        int sizeExponent = BitOperations.Log2((uint)colorCount); // colorCount == 2^sizeExponent
        byte packed = (byte)(0x80 | ((sizeExponent - 1) & 0x07));

        return (packed, colorTable, imageData);
    }

    private static void WriteHeaderAndLoop(Stream s, int width, int height)
    {
        WriteAscii(s, "GIF89a");
        WriteUInt16LE(s, (ushort)width);
        WriteUInt16LE(s, (ushort)height);
        s.WriteByte(0x00); // no global color table — every frame carries its own
        s.WriteByte(0x00); // background color index
        s.WriteByte(0x00); // pixel aspect ratio

        // NETSCAPE2.0 application extension: loop forever.
        s.WriteByte(0x21); s.WriteByte(0xFF); s.WriteByte(0x0B);
        WriteAscii(s, "NETSCAPE2.0");
        s.WriteByte(0x03); s.WriteByte(0x01);
        WriteUInt16LE(s, 0);
        s.WriteByte(0x00);
    }

    private static void WriteGraphicControlExtension(Stream s, int delayCs)
    {
        s.WriteByte(0x21); s.WriteByte(0xF9); s.WriteByte(0x04);
        s.WriteByte(0x04); // disposal method 1 (do not dispose) — each frame fully covers the canvas
        WriteUInt16LE(s, (ushort)delayCs);
        s.WriteByte(0x00); // transparent color index (unused)
        s.WriteByte(0x00);
    }

    private static void WriteImageDescriptorAndData(Stream s, int width, int height, byte packed, byte[] colorTable, byte[] imageData)
    {
        s.WriteByte(0x2C);
        WriteUInt16LE(s, 0);
        WriteUInt16LE(s, 0);
        WriteUInt16LE(s, (ushort)width);
        WriteUInt16LE(s, (ushort)height);
        s.WriteByte(packed);
        s.Write(colorTable, 0, colorTable.Length);
        s.Write(imageData, 0, imageData.Length);
    }

    private static void WriteAscii(Stream s, string text)
    {
        foreach (char c in text) s.WriteByte((byte)c);
    }

    private static void WriteUInt16LE(Stream s, ushort v)
    {
        s.WriteByte((byte)(v & 0xFF));
        s.WriteByte((byte)(v >> 8));
    }
}
