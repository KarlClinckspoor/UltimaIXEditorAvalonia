using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace U9Editor.Core
{
    /// <summary>
    /// Minimal PNG encoder: 8-bit grayscale, RGB or RGBA, no filtering. The output depends only on the pixels (no
    /// time stamps), so the same picture always gives the same bytes.
    /// </summary>
    public static class PngWriter
    {
        public static void WriteRgba(string path, int width, int height, byte[] pixels) => Write(path, width, height, pixels, 6, 4);
        public static void WriteGray(string path, int width, int height, byte[] pixels) => Write(path, width, height, pixels, 0, 1);
        public static void WriteRgb(string path, int width, int height, byte[] pixels) => Write(path, width, height, pixels, 2, 3);

        /// <summary>An RGBA picture as PNG bytes.</summary>
        public static byte[] EncodeRgba(int width, int height, byte[] pixels)
        {
            var ms = new MemoryStream();
            Write(ms, width, height, pixels, 6, 4);
            return ms.ToArray();
        }

        static void Write(string path, int width, int height, byte[] pixels, byte colorType, int bpp)
        {
            using var fs = File.Create(path);
            Write(fs, width, height, pixels, colorType, bpp);
        }

        static void Write(Stream fs, int width, int height, byte[] pixels, byte colorType, int bpp)
        {
            fs.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 });
            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8; ihdr[9] = colorType;
            Chunk(fs, "IHDR", ihdr);
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                    for (int y = 0; y < height; y++)
                    {
                        z.WriteByte(0);
                        z.Write(pixels, y * width * bpp, width * bpp);
                    }
                Chunk(fs, "IDAT", raw.ToArray());
            }
            Chunk(fs, "IEND", Array.Empty<byte>());
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);
            var t = Encoding.ASCII.GetBytes(type);
            s.Write(t);
            s.Write(data);
            uint crc = Crc(t, 0xFFFFFFFF);
            crc = Crc(data, crc) ^ 0xFFFFFFFF;
            var c = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(c, crc);
            s.Write(c);
        }

        static uint[] table;
        static uint Crc(byte[] data, uint crc)
        {
            if (table == null)
            {
                table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
            }
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
