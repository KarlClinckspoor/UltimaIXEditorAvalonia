using System;
using System.Buffers.Binary;
using System.IO;
using U9.Formats.Flx;

namespace U9.Formats.Text
{
    /// <summary>
    /// A game font's metrics: a record of static/bitmap16.flx (or bitmapsh / bitmapC; the same metrics) holding
    /// u32 max width, u32 height, u32 count (224), u32 flags, then 224 × {u32 offset, u32 size} and the glyphs, whose
    /// first byte is the advance. Glyph i draws byte 0x20 + i of the Windows-1252 string the game makes from a line.
    /// Subtitles and replies use record 0x263 (font slot 0). docs/exe-research.md, "Dialogue".
    /// </summary>
    public sealed class GameFont
    {
        public const int SubtitleRecord = 0x263, Glyphs = 224;
        public int MaxWidth { get; }
        public int Height { get; }
        readonly byte[] advance = new byte[Glyphs];
        readonly bool[] blank = new bool[Glyphs];

        GameFont(byte[] r)
        {
            if (r == null || r.Length < 0x10 + 8 * Glyphs) throw new InvalidDataException("not a font record");
            MaxWidth = BinaryPrimitives.ReadInt32LittleEndian(r);
            Height = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(4));
            if (BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(8)) != Glyphs) throw new InvalidDataException("a font record has 224 glyphs");
            ReadOnlySpan<byte> Glyph(int i)
            {
                int off = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(0x10 + 8 * i));
                int size = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(0x14 + 8 * i));
                return off > 0 && size > 0 && off + size <= r.Length ? r.AsSpan(off, size) : ReadOnlySpan<byte>.Empty;
            }
            for (int i = 0; i < Glyphs; i++)
            {
                var g = Glyph(i);
                advance[i] = g.Length > 0 ? g[0] : (byte)0;
                // A character the font has no shape for draws nothing: every pixel transparent (the empty glyphs are
                // 1 x 1 colour-keyed frames, like the space).
                blank[i] = i > 0 && Invisible(r, i);
            }
        }

        static bool Invisible(byte[] record, int glyph)
        {
            try
            {
                var f = U9.Formats.Bitmaps.Bitmap16.ReadFrame(record, glyph);
                for (int k = 3; k < f.Rgba.Length; k += 4) if (f.Rgba[k] != 0) return false;
                return true;
            }
            catch (InvalidDataException) { return true; }
        }

        public static GameFont Read(byte[] record) => new GameFont(record);

        public static GameFont Load(string bitmapFlx, int record = SubtitleRecord)
        {
            using var a = FlxArchive.Open(bitmapFlx);
            return new GameFont(a.Read(record));
        }

        /// <summary>The advance of a byte of the converted string (0 for control bytes).</summary>
        public int Advance(byte b) => b < 0x20 ? 0 : advance[b - 0x20];
        public bool Blank(byte b) => b >= 0x20 && blank[b - 0x20];
    }
}
