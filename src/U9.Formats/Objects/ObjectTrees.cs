using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace U9.Formats.Objects
{
    /// <summary>
    /// An object with everything that hangs off it, independent of any file: its record, its chain of 16-byte
    /// property segments (first first) and the objects inside it, each again a tree. The file offsets that tie
    /// them together (the record's segment offset, Next Property Pointer, Contents, a contained record's link to
    /// the next) are written afresh by <see cref="ObjectMap.AddTree"/>, so a tree can go into another chunk or file.
    /// </summary>
    public sealed class ObjectTree
    {
        /// <summary>The record (24 or 32 bytes) as it was; position and offsets are replaced when it is added.</summary>
        public byte[] Record;
        /// <summary>Nonfixed: the property segments, first first (empty for none).</summary>
        public List<byte[]> Segments = new List<byte[]>();
        /// <summary>Nonfixed: each Contents property (segment index, place 0-2) and the objects in it, in order.</summary>
        public List<TreeContents> Contents = new List<TreeContents>();

        public int Type => BinaryPrimitives.ReadUInt16LittleEndian(Record.AsSpan(0x0A));
        /// <summary>Every tree inside this one, at any depth.</summary>
        public IEnumerable<ObjectTree> Inside => Contents.SelectMany(c => c.Items).SelectMany(t => new[] { t }.Concat(t.Inside));

        public ObjectTree Clone() => new ObjectTree
        {
            Record = (byte[])Record.Clone(),
            Segments = Segments.Select(s => (byte[])s.Clone()).ToList(),
            Contents = Contents.Select(c => new TreeContents { Segment = c.Segment, Place = c.Place, Items = c.Items.Select(i => i.Clone()).ToList() }).ToList(),
        };

        /// <summary>The properties stored in the segments (pointers left out): id, A, B, segment index and place.</summary>
        public IEnumerable<(int Id, int A, int B, int Segment, int Place)> Properties()
        {
            for (int s = 0; s < Segments.Count; s++)
            {
                var seg = Segments[s];
                int n = Math.Min(3, (int)seg[0]);
                for (int k = 0; k < n; k++)
                {
                    int id = seg[1 + k];
                    if (id == PropertyNames.NextPropertyPointer) continue;
                    yield return (id, BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(4 + 4 * k)), BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(6 + 4 * k)), s, k);
                }
            }
        }

        /// <summary>Sets a property's parameter (0: A, 1: B) in its segment.</summary>
        public void SetParam(int segment, int place, int which, int value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(Segments[segment].AsSpan(4 + 4 * place + 2 * which), (ushort)value);

        /// <summary>Nonfixed: the link number (+0x1A).</summary>
        public int Link
        {
            get => Record.Length >= 0x1C ? BinaryPrimitives.ReadUInt16LittleEndian(Record.AsSpan(0x1A)) : -1;
            set => BinaryPrimitives.WriteUInt16LittleEndian(Record.AsSpan(0x1A), (ushort)value);
        }
    }

    public sealed class TreeContents
    {
        public int Segment, Place;
        public List<ObjectTree> Items = new List<ObjectTree>();
    }

    public sealed partial class ObjectMap
    {
        const int MaxTreeDepth = 8, MaxContents = 1000;

        /// <summary>An object with its property chain and contents, as a tree no longer tied to this file.</summary>
        public ObjectTree TreeOf(MapObject o) => TreeAt(o.FileOffset, 0);

        ObjectTree TreeAt(int rec, int depth)
        {
            var t = new ObjectTree { Record = Data.AsSpan(rec, RecordSize).ToArray() };
            if (Kind != ObjectFileKind.Nonfixed) return t;
            int seg = (int)(U32(rec + 0x1C) & ~1u);
            var seen = new HashSet<int>();
            while (seg != 0 && DataStart + (long)seg + 16 <= Data.Length && seen.Add(seg) && t.Segments.Count < 64)
            {
                var s = Data.AsSpan(DataStart + seg, 16).ToArray();
                int index = t.Segments.Count, next = 0;
                t.Segments.Add(s);
                int n = Math.Min(3, (int)s[0]);
                for (int k = 0; k < n; k++)
                {
                    int a = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(4 + 4 * k)), b = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(6 + 4 * k));
                    if (s[1 + k] == PropertyNames.NextPropertyPointer) next = a | (b << 16);
                    else if (s[1 + k] == PropertyNames.Contents && depth < MaxTreeDepth)
                    {
                        var c = new TreeContents { Segment = index, Place = k };
                        int at = a | (b << 16);
                        var items = new HashSet<int>();
                        while (at != 0 && DataStart + (long)at + RecordSize <= Data.Length && items.Add(at) && c.Items.Count < MaxContents)
                        {
                            c.Items.Add(TreeAt(DataStart + at, depth + 1));
                            at = (int)(U32(DataStart + at) & ~1u);
                        }
                        t.Contents.Add(c);
                    }
                }
                seg = next;
            }
            return t;
        }

        /// <summary>
        /// Adds an object tree at a position across the map (object units): the record goes into its chunk's list,
        /// its segments and the records inside it into the same chunk's pages, with their offsets written afresh.
        /// Every record and segment is counted in its page, as the game's files count contained items too.
        /// Undoable inside an edit, like <see cref="Add"/>.
        /// </summary>
        public MapObject AddTree(ObjectTree tree, int mapX, int mapY, int z)
        {
            if (tree?.Record == null || tree.Record.Length != RecordSize) throw new ArgumentException($"A record is {RecordSize} bytes", nameof(tree));
            if (Kind != ObjectFileKind.Nonfixed && (tree.Segments.Count > 0 || tree.Contents.Count > 0)) throw new ArgumentException("Only nonfixed objects carry properties and contents", nameof(tree));
            if (mapX < 0 || mapY < 0 || mapX >= Width * ChunkUnits || mapY >= Height * ChunkUnits) throw new ArgumentOutOfRangeException(nameof(mapX), "Outside the map");
            if ((uint)z > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(z));
            int chunk = mapX / ChunkUnits + mapY / ChunkUnits * Width;
            int x = mapX % ChunkUnits, y = mapY % ChunkUnits;
            int rec = WriteTree(chunk, tree, 0);
            var n = new MapObject
            {
                Owner = this, FileOffset = rec, ChunkX = chunk % Width * ChunkUnits, ChunkY = chunk / Width * ChunkUnits,
                Subchunk = x / SubchunkUnits + y / SubchunkUnits * 4, Removed = true,
            };
            Touch(n);
            n.Removed = false;
            byOffset[rec] = n;
            Objects.Add(n);
            LinkHead(chunk, n.Subchunk, n);
            PutU16(rec + 4, x);
            PutU16(rec + 6, y);
            PutU16(rec + 8, z);
            return n;
        }

        /// <summary>Writes a tree's record, segments and contents into a chunk's pages, unlinked; returns the record's file offset.</summary>
        int WriteTree(int chunk, ObjectTree t, int depth)
        {
            if (depth > MaxTreeDepth) throw new ArgumentException("Contents nest too deep");
            int rec = Allocate(chunk, RecordSize);
            CountRecords(rec, +1);
            var raw = (byte[])t.Record.Clone();
            // the list link (or, inside a container, the link to the next item) is set by the caller; keep its flag bit
            BinaryPrimitives.WriteUInt32LittleEndian(raw, BinaryPrimitives.ReadUInt32LittleEndian(raw) & 1u & (depth > 0 ? 1u : 0u));
            Put(rec, raw);          // claims the space before the segments are allocated
            if (Kind != ObjectFileKind.Nonfixed) return rec;
            uint flag = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x1C)) & 1u;
            if (t.Segments.Count == 0) { PutU32(rec + 0x1C, flag); return rec; }
            var segs = t.Segments.Select(s => (byte[])s.Clone()).ToList();
            var at = new int[segs.Count];
            for (int i = 0; i < segs.Count; i++)
            {
                at[i] = Allocate(chunk, 16);
                Put(at[i], segs[i]);            // claimed; pointers filled in below
                CountProperties(at[i], +1);
            }
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                int n = Math.Min(3, (int)s[0]);
                for (int k = 0; k < n; k++)
                    if (s[1 + k] == PropertyNames.NextPropertyPointer)
                        Offset32(s, k, i + 1 < segs.Count ? at[i + 1] - DataStart : 0);
            }
            foreach (var c in t.Contents)
            {
                if (c.Segment < 0 || c.Segment >= segs.Count) continue;
                int first = 0, prev = -1;
                foreach (var item in c.Items)
                {
                    int r = WriteTree(chunk, item, depth + 1);
                    if (prev < 0) first = r - DataStart;
                    else PutU32(prev, (U32(prev) & 1u) | (uint)(r - DataStart));
                    prev = r;
                }
                Offset32(segs[c.Segment], c.Place, first);
            }
            for (int i = 0; i < segs.Count; i++) Put(at[i], segs[i]);
            PutU32(rec + 0x1C, (uint)(at[0] - DataStart) | flag);
            return rec;
        }

        static void Offset32(byte[] seg, int place, int offset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(seg.AsSpan(4 + 4 * place), (ushort)(offset & 0xFFFF));
            BinaryPrimitives.WriteUInt16LittleEndian(seg.AsSpan(6 + 4 * place), (ushort)((uint)offset >> 16));
        }
    }
}
