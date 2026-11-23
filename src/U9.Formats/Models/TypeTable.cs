using System;
using System.Buffers.Binary;
using System.IO;

namespace U9.Formats.Models
{
    /// <summary>One object type's entry in static/TYPES.DAT.</summary>
    public readonly struct TypeInfo
    {
        public readonly int Usecode, DefaultModel, Flags, Weight, Volume, Book, HitPoints;
        public TypeInfo(int usecode, int model, int flags, int weight, int volume, int book, int hp)
        { Usecode = usecode; DefaultModel = model; Flags = flags; Weight = weight; Volume = volume; Book = book; HitPoints = hp; }
    }

    /// <summary>
    /// static/TYPES.DAT: an 8-byte header (u32 5784, u32 352; meaning unknown), then 16 bytes per type number:
    /// +0 u32 (0 or 0xCDCDCDCD, ignore), +4 u16 usecode, +6 u16 default model (sappear.flx record), +8 u16 flags
    /// (0x01 never hidden, 0x02 NPC-only collision, 0x04 partial collision, 0x08 does not block the camera, 0x10
    /// portal block, 0x20 unique model, 0x100 mesh collision), +0xA u8 weight (254/255 immovable), +0xB u8 volume,
    /// +0xC u8 book, +0xD u8 hit points, +0xE u16 zero. Type 8191 (0x1FFF) is a placeholder whose model 0 is a grey box.
    /// </summary>
    public sealed class TypeTable
    {
        public const int PlaceholderType = 0x1FFF;
        readonly TypeInfo[] types;

        TypeTable(TypeInfo[] t) { types = t; }

        public int Count => types.Length;
        public TypeInfo this[int type] => (uint)type < (uint)types.Length ? types[type] : default;

        public static TypeTable Load(string path)
        {
            var d = File.ReadAllBytes(path);
            int n = (d.Length - 8) / 16;
            var t = new TypeInfo[n];
            for (int i = 0; i < n; i++)
            {
                var r = d.AsSpan(8 + 16 * i);
                t[i] = new TypeInfo(BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(4)), BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(6)),
                                    BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(8)), r[0xA], r[0xB], r[0xC], r[0xD]);
            }
            return new TypeTable(t);
        }
    }
}
