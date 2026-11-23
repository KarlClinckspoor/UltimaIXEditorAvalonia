using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace U9.Formats.Objects
{
    public enum ObjectFileKind
    {
        /// <summary>static/fixed.N: scenery that never moves. 24-byte records.</summary>
        Fixed,
        /// <summary>runtime/nonfixed.N: items, NPCs, triggers. 32-byte records.</summary>
        Nonfixed,
    }

    /// <summary>One object record, read in place from an <see cref="ObjectMap"/>.</summary>
    public sealed class MapObject
    {
        internal ObjectMap Owner;
        /// <summary>Offset of the record from the start of the file.</summary>
        public int FileOffset { get; internal set; }
        /// <summary>Base of the chunk the record is filed under, in object units (128 per terrain point).</summary>
        public int ChunkX { get; internal set; }
        public int ChunkY { get; internal set; }
        /// <summary>Which of the chunk's 16 lists (4 x 4 subchunks) it is linked in.</summary>
        public int Subchunk { get; internal set; }

        Span<byte> Rec => Owner.Data.AsSpan(FileOffset, Owner.RecordSize);

        /// <summary>Position inside the chunk, 0-4095 (128 units per point).</summary>
        public int X => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(4));
        public int Y => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(6));
        /// <summary>Height in object units: 4 x the terrain height scale.</summary>
        public int Z => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(8));
        public int Type => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(0x0A));
        /// <summary>The three stored quaternion components (x, y, z) at +0x0C, as signed 0.15 fixed point.</summary>
        public (short X, short Y, short Z) Rotation =>
            (BinaryPrimitives.ReadInt16LittleEndian(Rec.Slice(0x0C)), BinaryPrimitives.ReadInt16LittleEndian(Rec.Slice(0x0E)), BinaryPrimitives.ReadInt16LittleEndian(Rec.Slice(0x10)));
        /// <summary>Fixed: u16 flags at +0x12. Nonfixed: u32 flags at +0x14.</summary>
        public uint Flags => Owner.Kind == ObjectFileKind.Fixed
            ? BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(0x12))
            : BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(0x14));

        /// <summary>The raw record bytes (a copy).</summary>
        public byte[] Raw => Rec.ToArray();

        /// <summary>Position in terrain points (x east, row) and terrain height.</summary>
        public double PointX => (ChunkX + X) / 128.0;
        public double PointRow => (ChunkY + Y) / 128.0;
        public double TerrainHeight => Z / 4.0;

        /// <summary>Moves the object inside its chunk (0-4095 on x and y). Crossing chunks needs relinking, not done yet.</summary>
        public void SetPosition(int x, int y, int z)
        {
            if ((uint)x > 4095 || (uint)y > 4095 || (uint)z > 0xFFFF) throw new ArgumentOutOfRangeException();
            BinaryPrimitives.WriteUInt16LittleEndian(Rec.Slice(4), (ushort)x);
            BinaryPrimitives.WriteUInt16LittleEndian(Rec.Slice(6), (ushort)y);
            BinaryPrimitives.WriteUInt16LittleEndian(Rec.Slice(8), (ushort)z);
        }

        public override string ToString() => $"type {Type} at ({PointX:F2}, {PointRow:F2}) z {Z}";
    }

    /// <summary>
    /// The objects of one map: static/fixed.N or runtime/nonfixed.N. Both share one layout:
    /// <list type="bullet">
    /// <item>Header. Fixed: 0x1C bytes (0x08 data size, 0x0C 0x00C00000, 0x10 width, 0x14 height in chunks).
    /// Nonfixed: 0x20 bytes (0x0C data size, 0x10 0x00C00000, 0x14 width, 0x18 height, 0x1C 1).</item>
    /// <item>Chunk table: width x height u32, x + y * width. Bit 0 is a flag (set on every used entry); the rest
    /// is an offset from the data start, 0 for an empty chunk.</item>
    /// <item>4 more bytes, then the data (data start = table end + 4; file length = data start + data size).</item>
    /// <item>Data: 4 KB pages. Page header: +0x00 next page of the same chunk (bit 0 flag), +0x04 free object space,
    /// +0x08 free property space, +0x0C chunk base x, +0x10 chunk base y (object units, a chunk is 4096 = 32
    /// points), +0x14 object count in this page, +0x20 16 u32 list heads, one per 4 x 4-tile subchunk (offsets from
    /// the data start), each the start of a list linked through the record's first u32.</item>
    /// <item>Record: +0x00 next, +0x04 x, +0x06 y, +0x08 z (u16; x and y from the chunk base, 128 per point; z is
    /// 4 x the terrain height), +0x0A type, +0x0C three int16 quaternion parts. Fixed (24 bytes): +0x12 u16 flags,
    /// +0x14 u32 unknown. Nonfixed (32 bytes): +0x14 u32 flags, +0x18 u16 model, +0x1A u16 link, +0x1C u32 property
    /// offset (from the public notes; to be confirmed).</item>
    /// </list>
    /// Checked on Britannia: every page's base matches its table slot, and objects stand on the terrain (median
    /// |z/4 - terrain height| = 14.5 for fixed.9). Lists can run into the chunk's other pages, so records are
    /// collected once each. The file is kept as bytes and edited in place, so it always writes back unchanged.
    /// </summary>
    public sealed class ObjectMap
    {
        public ObjectFileKind Kind { get; }
        public byte[] Data { get; }
        public int Width { get; }
        public int Height { get; }
        public int DataStart { get; }
        public int RecordSize => Kind == ObjectFileKind.Fixed ? 24 : 32;
        public List<MapObject> Objects { get; } = new List<MapObject>();
        /// <summary>List links that pointed outside the file (skipped).</summary>
        public int BadLinks { get; private set; }

        ObjectMap(ObjectFileKind kind, byte[] data)
        {
            Kind = kind;
            Data = data;
            int sizeAt = kind == ObjectFileKind.Fixed ? 0x10 : 0x14;
            int table = kind == ObjectFileKind.Fixed ? 0x1C : 0x20;
            if (data.Length < table) throw new InvalidDataException("File is shorter than an object file header");
            Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sizeAt));
            Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sizeAt + 4));
            if (Width < 0 || Height < 0 || Width > 256 || Height > 256) throw new InvalidDataException($"Object file size {Width}x{Height} chunks is not plausible");
            DataStart = table + 4 * Width * Height + 4;
            if (DataStart > data.Length) throw new InvalidDataException("Chunk table runs past the end of the file");

            var seen = new HashSet<uint>();
            var pages = new HashSet<uint>();
            for (int c = 0; c < Width * Height; c++)
            {
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(table + 4 * c));
                uint page = entry & ~1u;
                if (entry == 0) continue;
                while (pages.Add(page) && DataStart + page + 0x60 <= data.Length)
                {
                    var ph = data.AsSpan(DataStart + (int)page);
                    uint next = BinaryPrimitives.ReadUInt32LittleEndian(ph);
                    int cx = (int)BinaryPrimitives.ReadUInt32LittleEndian(ph.Slice(0x0C));
                    int cy = (int)BinaryPrimitives.ReadUInt32LittleEndian(ph.Slice(0x10));
                    for (int s = 0; s < 16; s++)
                    {
                        uint p = BinaryPrimitives.ReadUInt32LittleEndian(ph.Slice(0x20 + 4 * s));
                        for (int guard = 0; p != 0 && guard < 100000 && seen.Add(p); guard++)
                        {
                            long at = DataStart + (long)(p & ~1u);
                            if (at + RecordSize > data.Length) { BadLinks++; break; }
                            // a few lists run into property data (4 of Britannia's 15,467 nonfixed links): stop there
                            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)at + 4)) > 4095 ||
                                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)at + 6)) > 4095) { BadLinks++; break; }
                            Objects.Add(new MapObject { Owner = this, FileOffset = (int)at, ChunkX = cx, ChunkY = cy, Subchunk = s });
                            p = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at));
                        }
                    }
                    if (next == 0) break;
                    page = next & ~1u;
                }
            }
        }

        public static ObjectMap Load(string path, ObjectFileKind kind) => new ObjectMap(kind, File.ReadAllBytes(path));
        public static ObjectMap Read(byte[] data, ObjectFileKind kind) => new ObjectMap(kind, data);

        public byte[] ToBytes() => (byte[])Data.Clone();
        public void Save(string path) => File.WriteAllBytes(path, Data);
    }
}
