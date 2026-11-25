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

    /// <summary>The fields of a TYPES.DAT entry that can be edited.</summary>
    public enum TypeField { Usecode, DefaultModel, Flags, Weight, Volume, Book, HitPoints }

    /// <summary>
    /// static/TYPES.DAT: an 8-byte header (u32 5784, u32 352; meaning unknown), then 16 bytes per type number:
    /// +0 u32 (0 or 0xCDCDCDCD, ignore), +4 u16 usecode, +6 u16 default model (sappear.flx record), +8 u16 flags
    /// (0x01 never hidden, 0x02 NPC-only collision, 0x04 partial collision, 0x08 does not block the camera, 0x10
    /// portal block, 0x20 unique model, 0x100 mesh collision), +0xA u8 weight (254/255 immovable), +0xB u8 volume,
    /// +0xC u8 book, +0xD u8 hit points, +0xE u16 zero. Type 8191 (0x1FFF) is a placeholder whose model 0 is a grey box.
    /// <para>The file is kept as bytes and edited in place, so it writes back unchanged unless edited.</para>
    /// </summary>
    public sealed class TypeTable
    {
        public const int PlaceholderType = 0x1FFF, HeaderSize = 8, EntrySize = 16;
        readonly byte[] data;

        TypeTable(byte[] d, string path) { data = d; Path = path; }

        /// <summary>Where it was loaded from (null when read from bytes).</summary>
        public string Path { get; }
        public int Count => (data.Length - HeaderSize) / EntrySize;

        public TypeInfo this[int type]
        {
            get
            {
                if ((uint)type >= (uint)Count) return default;
                var r = data.AsSpan(HeaderSize + EntrySize * type);
                return new TypeInfo(BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(4)), BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(6)),
                                    BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(8)), r[0xA], r[0xB], r[0xC], r[0xD]);
            }
        }

        public static TypeTable Load(string path) => new TypeTable(File.ReadAllBytes(path), path);
        public static TypeTable Read(byte[] bytes) => new TypeTable((byte[])bytes.Clone(), null);

        public byte[] ToBytes() => (byte[])data.Clone();
        public void Save() => Save(Path ?? throw new InvalidOperationException("Loaded from bytes: give a path"));
        public void Save(string path)
        {
            string temp = path + ".new";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, true);
        }

        static (int Offset, int Size) Where(TypeField f) => f switch
        {
            TypeField.Usecode => (4, 2), TypeField.DefaultModel => (6, 2), TypeField.Flags => (8, 2),
            TypeField.Weight => (0xA, 1), TypeField.Volume => (0xB, 1), TypeField.Book => (0xC, 1), TypeField.HitPoints => (0xD, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        /// <summary>The largest value a field holds (255 for the byte fields, 65535 for the others).</summary>
        public static int MaxOf(TypeField f) => Where(f).Size == 1 ? 0xFF : 0xFFFF;

        public int Get(int type, TypeField f)
        {
            if ((uint)type >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(type));
            var (o, n) = Where(f);
            int at = HeaderSize + EntrySize * type + o;
            return n == 1 ? data[at] : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        }

        public void Set(int type, TypeField f, int value)
        {
            if ((uint)type >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(type));
            if ((uint)value > (uint)MaxOf(f)) throw new ArgumentOutOfRangeException(nameof(value), $"{f} is 0 to {MaxOf(f)}");
            var (o, n) = Where(f);
            int at = HeaderSize + EntrySize * type + o;
            if (n == 1) data[at] = (byte)value; else BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)value);
        }
    }
}
