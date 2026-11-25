using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace U9.Formats.Text
{
    /// <summary>
    /// The header of a sound/Speech.flx record: the text.flx line it voices, the original clip name
    /// ("Exferlem_07143.umt": speaker and script line number), and the sound format. Layout: u32 line, char[32] name,
    /// u32 (0), u32 data length, u32 rate (22050), u32 bits (16), u32 channels (1), u32 codec (0 PCM, 1 EA-XA ADPCM, 2 EA MicroTalk); data
    /// from 0x3C (layout as TITAN documents it, checked against all 7,010 clips).
    /// </summary>
    public readonly record struct SpeechClip(int Record, int Line, string Name, int DataLength, int Rate, int Bits, int Channels, int Codec, int Extra, int RecordLength)
    {
        public const int HeaderSize = 0x3C;
        /// <summary>Codec 2 (EA MicroTalk, see <see cref="MicroTalk"/>) data starts with a u32 sample count.</summary>
        /// <summary>The speaker part of the clip name ("Exferlem").</summary>
        public string Speaker
        {
            get
            {
                string n = Name ?? "";
                int u = n.LastIndexOf('_');
                return u > 0 ? n[..u] : n;
            }
        }

        public static SpeechClip? Parse(int record, ReadOnlySpan<byte> b, int recordLength)
        {
            if (b.Length < HeaderSize) return null;
            var raw = b.Slice(4, 32);
            int z = raw.IndexOf((byte)0);
            string name = Encoding.ASCII.GetString(z < 0 ? raw : raw[..z]);
            static int I(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o));
            return new SpeechClip(record, I(b, 0), name, I(b, 0x28), I(b, 0x2C), I(b, 0x30), I(b, 0x34), I(b, 0x38), I(b, 0x24), recordLength);
        }
    }

    /// <summary>The clips of sound/Speech.flx, read from the headers only (the file is 140 MB).</summary>
    public static class SpeechTable
    {
        public static List<SpeechClip> ReadHeaders(string path)
        {
            using var f = File.OpenRead(path);
            var head = new byte[0x80];
            f.ReadExactly(head);
            int count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x50));
            var dir = new byte[8 * count];
            f.ReadExactly(dir);
            var list = new List<SpeechClip>();
            var h = new byte[SpeechClip.HeaderSize];
            for (int i = 0; i < count; i++)
            {
                uint off = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(8 * i));
                int len = BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(8 * i + 4));
                if (off == 0 || len < SpeechClip.HeaderSize) continue;
                f.Position = off;
                f.ReadExactly(h);
                if (SpeechClip.Parse(i, h, len) is SpeechClip c) list.Add(c);
            }
            return list;
        }

        /// <summary>A whole record (header and sound data), or null.</summary>
        public static byte[] ReadRecord(string path, int record)
        {
            using var f = File.OpenRead(path);
            var head = new byte[0x80];
            f.ReadExactly(head);
            int count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x50));
            if (record < 0 || record >= count) return null;
            var e = new byte[8];
            f.Position = 0x80 + 8 * record;
            f.ReadExactly(e);
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(e);
            int len = BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(4));
            if (off == 0 || len <= 0) return null;
            var b = new byte[len];
            f.Position = off;
            f.ReadExactly(b);
            return b;
        }
    }
}
