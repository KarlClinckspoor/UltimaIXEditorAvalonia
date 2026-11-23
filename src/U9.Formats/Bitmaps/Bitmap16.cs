using System;
using System.Buffers.Binary;
using System.IO;

namespace U9.Formats.Bitmaps
{
    /// <summary>One decoded frame: RGBA bytes, row 0 first as stored.</summary>
    public sealed class BitmapFrame
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Rgba { get; init; }
    }

    /// <summary>
    /// A record of static/bitmap16.flx: terrain and model textures, 16 bits per pixel.
    /// <para>Record header (little-endian):</para>
    /// <list type="bullet">
    /// <item>0x00 u16: largest frame width. 0x02 u16: unknown (3-5 for 32-128 pixel textures, maybe a mip count).</item>
    /// <item>0x04 u16: largest frame height. 0x06 u16: compression, 0 in every shipped bitmap16 record.</item>
    /// <item>0x08 u32: frame count. 0x0C: 4 bytes, unknown.</item>
    /// <item>0x10: per frame, (u32 offset from the record start, u32 length).</item>
    /// </list>
    /// <para>Frame: 0x00 u32 flags (bit 30: pixel format, see below; the rest unknown), 0x04 u32 width, 0x08 u32 height,
    /// 0x0C 8 bytes unknown (zero), 0x14 one u32 offset per row (from the frame start; 30 frames leave it unwritten, 0xCDCDCDCD, with the rows packed right after it), then the pixels, then
    /// smaller mipmaps (ignored here: the renderer builds its own).</para>
    /// <para>Pixel, when frame flag bit 30 is set (6013 of 6599 records, all terrain and most model textures): RGB565,
    /// opaque — bits 0-4 blue, 5-10 green, 11-15 red. When it is clear (interface pictures such as the world map,
    /// record 534): bits 0-4 blue, 5-9 green, 10-14 red, bit 15 set = opaque. Checked by eye on terrain textures
    /// 1, 2, 8, 10, 11, 24, 36, 46 (565 gives sand, rock and grass; 555 gives green noise) and on record 534.</para>
    /// <para>Terrain: a point's texture number is the record number and its frame number the frame.</para>
    /// </summary>
    public static class Bitmap16
    {
        public static int FrameCount(byte[] record)
        {
            if (record == null || record.Length < 0x10) return 0;
            return (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));
        }

        public static BitmapFrame ReadFrame(byte[] record, int frame)
        {
            var r = record.AsSpan();
            int count = FrameCount(record);
            if ((uint)frame >= (uint)count) throw new ArgumentOutOfRangeException(nameof(frame));
            if (BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(6)) != 0)
                throw new InvalidDataException("Compressed bitmap16 records are not supported");
            int fo = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(0x10 + frame * 8));
            int fl = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(0x14 + frame * 8));
            if (fo < 0 || fo + fl > record.Length || fl < 0x14) throw new InvalidDataException($"Frame {frame} lies outside the record");
            var f = r.Slice(fo, fl);
            int w = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(4));
            int h = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(8));
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || 0x14 + 4 * h > fl) throw new InvalidDataException($"Frame {frame} has a bad size {w}x{h}");
            bool rgb565 = (BinaryPrimitives.ReadUInt32LittleEndian(f) & 0x40000000u) != 0;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                // 30 shipped frames have the row table left as 0xCDCDCDCD (never written); their rows follow the table
                long ro = BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(0x14 + 4 * y));
                if (ro < 0x14 + 4 * h || ro + w * 2 > fl) ro = 0x14 + 4 * h + (long)y * w * 2;
                if (ro + w * 2 > fl) throw new InvalidDataException($"Row {y} of frame {frame} lies outside the frame");
                for (int x = 0; x < w; x++)
                {
                    ushort v = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice((int)ro + x * 2));
                    int o = (x + y * w) * 4;
                    if (rgb565)
                    {
                        rgba[o] = Expand(v >> 11);
                        rgba[o + 1] = (byte)(((v >> 5) & 63) << 2 | ((v >> 5) & 63) >> 4);
                        rgba[o + 2] = Expand(v & 31);
                        rgba[o + 3] = 255;
                    }
                    else
                    {
                        rgba[o] = Expand((v >> 10) & 31);
                        rgba[o + 1] = Expand((v >> 5) & 31);
                        rgba[o + 2] = Expand(v & 31);
                        rgba[o + 3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                    }
                }
            }
            return new BitmapFrame { Width = w, Height = h, Rgba = rgba };
        }

        static byte Expand(int c5) => (byte)((c5 << 3) | (c5 >> 2));
    }
}
