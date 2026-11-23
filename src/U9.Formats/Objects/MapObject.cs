using System;
using System.Buffers.Binary;
using System.Numerics;

namespace U9.Formats.Objects
{
    /// <summary>One object record, read and edited in place in an <see cref="ObjectMap"/>.</summary>
    public sealed class MapObject
    {
        internal ObjectMap Owner;
        /// <summary>The file the object is in.</summary>
        public ObjectMap Map => Owner;
        /// <summary>Offset of the record from the start of the file. Moving an object to another chunk moves its record.</summary>
        public int FileOffset { get; internal set; }
        /// <summary>Base of the chunk the record is filed under, in object units (128 per terrain point).</summary>
        public int ChunkX { get; internal set; }
        public int ChunkY { get; internal set; }
        /// <summary>Which of the chunk's 16 lists it is linked in: x / 1024 + 4 * (y / 1024) inside the chunk.</summary>
        public int Subchunk { get; internal set; }
        /// <summary>Deleted from the map (kept so an undo can bring it back).</summary>
        public bool Removed { get; internal set; }

        internal int Chunk => ChunkX / ObjectMap.ChunkUnits + ChunkY / ObjectMap.ChunkUnits * Owner.Width;
        Span<byte> Rec => Owner.Data.AsSpan(FileOffset, Owner.RecordSize);

        /// <summary>Position inside the chunk, 0-4095 (128 units per point).</summary>
        public int X => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(4));
        public int Y => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(6));
        /// <summary>Height in object units: 4 x the terrain height scale.</summary>
        public int Z => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(8));
        public int Type => BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(0x0A));
        /// <summary>Position across the whole map in object units (chunk base + position in the chunk).</summary>
        public int MapX => ChunkX + X;
        public int MapY => ChunkY + Y;

        /// <summary>
        /// Rotation quaternion stored at +0x0C as int16 (x, y, z, w), magnitude 32767; normalised here. Applied as is
        /// (not conjugated): checked on chains of fences and walls, and every fixed.9 quaternion has length 1.
        /// </summary>
        public Quaternion Rotation => new Quaternion(
            BinaryPrimitives.ReadInt16LittleEndian(Rec.Slice(0x0C)) / 32767f, BinaryPrimitives.ReadInt16LittleEndian(Rec.Slice(0x0E)) / 32767f,
            BinaryPrimitives.ReadInt16LittleEndian(Rec.Slice(0x10)) / 32767f, BinaryPrimitives.ReadInt16LittleEndian(Rec.Slice(0x12)) / 32767f);
        /// <summary>Fixed: u16 flags at +0x14. Nonfixed: u32 flags at +0x14.</summary>
        public uint Flags => Owner.Kind == ObjectFileKind.Fixed
            ? BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(0x14))
            : BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(0x14));

        /// <summary>Nonfixed only: model (sappear.flx record) at +0x18, overriding the type's default model.</summary>
        public int Model => Owner.Kind == ObjectFileKind.Nonfixed ? BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(0x18)) : -1;
        /// <summary>Nonfixed only: link number at +0x1A, which triggers use to find each other's objects (map change arrival markers).</summary>
        public int Link => Owner.Kind == ObjectFileKind.Nonfixed ? BinaryPrimitives.ReadUInt16LittleEndian(Rec.Slice(0x1A)) : -1;
        /// <summary>Nonfixed only: offset of the property segment from the data start, 0 for none.</summary>
        public int PropertyOffset => Owner.Kind == ObjectFileKind.Nonfixed ? (int)(BinaryPrimitives.ReadUInt32LittleEndian(Rec.Slice(0x1C)) & ~1u) : 0;
        /// <summary>Nonfixed only: the property segment, or null.</summary>
        public PropertySegment Properties
        {
            get
            {
                int p = PropertyOffset;
                long at = Owner.DataStart + (long)p;
                if (p == 0 || at + 16 > Owner.Data.Length) return null;
                return new PropertySegment(Owner.Data.AsSpan((int)at, 16));
            }
        }

        /// <summary>The raw record bytes (a copy).</summary>
        public byte[] Raw => Rec.ToArray();

        /// <summary>Position in terrain points (x east, row) and terrain height.</summary>
        public double PointX => MapX / 128.0;
        public double PointRow => MapY / 128.0;
        public double TerrainHeight => Z / 4.0;

        /// <summary>
        /// Moves the object to a position across the map, in object units. Inside its chunk only the position (and,
        /// across a subchunk border, the list it is linked in) changes; into another chunk the record moves to that
        /// chunk's pages. Inside an <see cref="ObjectMap.BeginEdit"/> the change can be undone.
        /// </summary>
        public void MoveTo(int mapX, int mapY, int z) => Owner.Move(this, mapX, mapY, z);

        /// <summary>Sets the rotation (normalised, stored as int16 x 32767).</summary>
        public void SetRotation(Quaternion q) => Owner.Rotate(this, q);

        public override string ToString() => $"type {Type} at ({PointX:F2}, {PointRow:F2}) z {Z}";
    }
}
