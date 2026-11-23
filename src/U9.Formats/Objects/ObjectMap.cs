using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace U9.Formats.Objects
{
    public enum ObjectFileKind
    {
        /// <summary>static/fixed.N: scenery that never moves. 24-byte records.</summary>
        Fixed,
        /// <summary>runtime/nonfixed.N: items, NPCs, triggers. 32-byte records.</summary>
        Nonfixed,
    }

    /// <summary>
    /// The objects of one map: static/fixed.N or runtime/nonfixed.N. Both share one layout:
    /// <list type="bullet">
    /// <item>Header. Fixed: 0x1C bytes (0x08 data size, 0x0C 0x00C00000, 0x10 width, 0x14 height in chunks).
    /// Nonfixed: 0x20 bytes (0x0C data size, 0x10 0x00C00000, 0x14 width, 0x18 height, 0x1C 1).</item>
    /// <item>Chunk table: width x height u32, x + y * width: the chunk's first page | 1 (offset from the data
    /// start), 0 for an empty chunk. A chunk is 4096 object units (32 points) square.</item>
    /// <item>u32 free page list: the first unused page | 1, 0 for none; an unused page's +0x00 holds the next.
    /// Then the data (data start = table end + 4; file length = data start + data size).</item>
    /// <item>Data: 4 KB pages. A chunk is a chain of pages. Page header: +0x00 next page of the chunk | 1 (0 at the
    /// end), +0x04 offset of the page's first free byte, +0x08 free property list (nonfixed), +0x0C chunk base x,
    /// +0x10 chunk base y (object units), +0x14 records in this page, +0x18 property segments in this page
    /// (nonfixed), +0x20 16 u32 list heads, one per 4 x 4-tile subchunk (x / 1024 + 4 * (y / 1024) inside the
    /// chunk). Only the chunk's first page's heads are used (later pages hold leftovers there); a list runs
    /// through records in any of the chunk's pages, linked through the record's first u32 (offsets from the data
    /// start, 0 at the end). New records and property segments go at the page's free offset.</item>
    /// <item>Record: +0x00 next, +0x04 x, +0x06 y, +0x08 z (u16; x and y from the chunk base, 128 per point; z is
    /// 4 x the terrain height), +0x0A type, +0x0C int16 quaternion (x, y, z, w). Fixed (24 bytes, packed from
    /// +0x60): +0x14 u16 flags, +0x16 u16 unknown. Nonfixed (32 bytes): +0x14 u32 flags, +0x18 u16 model, +0x1A
    /// u16 link, +0x1C u32 property segment offset (16 bytes, usually in the record's page).</item>
    /// </list>
    /// Checked on every shipped file: every record in a first page's lists is filed under the subchunk its
    /// position gives, and the records counted in a chunk's pages add up to those in its lists. The file is kept as
    /// bytes and edited in place, so it writes back unchanged unless edited.
    /// </summary>
    public sealed class ObjectMap
    {
        public const int PageSize = 4096, PageHeader = 0x60, ChunkUnits = 4096, SubchunkUnits = 1024;

        public ObjectFileKind Kind { get; }
        public byte[] Data { get; private set; }
        public int Width { get; }
        public int Height { get; }
        public int DataStart { get; }
        public int RecordSize => Kind == ObjectFileKind.Fixed ? 24 : 32;
        /// <summary>The map's objects (removed ones are taken out).</summary>
        public List<MapObject> Objects { get; } = new List<MapObject>();
        /// <summary>List links that pointed outside the file or at something other than a record (skipped).</summary>
        public int BadLinks { get; private set; }

        int TableStart => Kind == ObjectFileKind.Fixed ? 0x1C : 0x20;
        int DataSizeAt => Kind == ObjectFileKind.Fixed ? 0x08 : 0x0C;
        int FreePagesAt => DataStart - 4;

        /// <summary>Per chunk, its pages (offsets from the data start), first page first.</summary>
        readonly List<int>[] chunkPages;
        readonly Dictionary<int, MapObject> byOffset = new Dictionary<int, MapObject>();

        ObjectMap(ObjectFileKind kind, byte[] data)
        {
            Kind = kind;
            Data = data;
            int sizeAt = kind == ObjectFileKind.Fixed ? 0x10 : 0x14;
            if (data.Length < TableStart) throw new InvalidDataException("File is shorter than an object file header");
            Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sizeAt));
            Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sizeAt + 4));
            if (Width < 0 || Height < 0 || Width > 256 || Height > 256) throw new InvalidDataException($"Object file size {Width}x{Height} chunks is not plausible");
            DataStart = TableStart + 4 * Width * Height + 4;
            if (DataStart > data.Length) throw new InvalidDataException("Chunk table runs past the end of the file");

            chunkPages = new List<int>[Width * Height];
            var allPages = new HashSet<int>();
            var seen = new HashSet<int>();
            for (int c = 0; c < Width * Height; c++)
            {
                var pages = chunkPages[c] = new List<int>();
                uint entry = U32(TableStart + 4 * c);
                if (entry == 0) continue;
                int page = (int)(entry & ~1u);
                // a few pages hold something other than a page offset in +0x00: follow only page-aligned values
                while ((page & (PageSize - 1)) == 0 && DataStart + (long)page + PageHeader <= data.Length && allPages.Add(page))
                {
                    pages.Add(page);
                    uint next = U32(DataStart + page);
                    if (next == 0) break;
                    page = (int)(next & ~1u);
                }
                if (pages.Count == 0) continue;
                int cx = c % Width * ChunkUnits, cy = c / Width * ChunkUnits;
                for (int s = 0; s < 16; s++)
                {
                    int p = (int)U32(DataStart + pages[0] + 0x20 + 4 * s);
                    for (int guard = 0; p != 0 && guard < 100000 && seen.Add(p); guard++)
                    {
                        long at = DataStart + (long)(p & ~1);
                        if (p < 0 || at + RecordSize > data.Length) { BadLinks++; break; }
                        // a few lists run into property data (4 of Britannia's 15,467 nonfixed links): stop there
                        if (U16((int)at + 4) > 4095 || U16((int)at + 6) > 4095) { BadLinks++; break; }
                        var o = new MapObject { Owner = this, FileOffset = (int)at, ChunkX = cx, ChunkY = cy, Subchunk = s };
                        Objects.Add(o);
                        byOffset[o.FileOffset] = o;
                        p = (int)U32((int)at);
                    }
                }
            }
        }

        public static ObjectMap Load(string path, ObjectFileKind kind) => new ObjectMap(kind, File.ReadAllBytes(path));
        public static ObjectMap Read(byte[] data, ObjectFileKind kind) => new ObjectMap(kind, data);

        public byte[] ToBytes() => (byte[])Data.Clone();
        public void Save(string path) => File.WriteAllBytes(path, Data);

        /// <summary>The pages of a chunk (offsets from the data start), first page first.</summary>
        public IReadOnlyList<int> PagesOf(int chunkX, int chunkY) => chunkPages[chunkX + chunkY * Width];

        // ---- reading and journalled writing

        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at));
        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at));

        ObjectEdit journal;

        void Put(int at, ReadOnlySpan<byte> bytes)
        {
            if (journal != null) journal.Writes.Add((at, Data.AsSpan(at, bytes.Length).ToArray(), bytes.ToArray()));
            bytes.CopyTo(Data.AsSpan(at));
        }

        void PutU32(int at, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            Put(at, b);
        }

        void PutU16(int at, int v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
            Put(at, b);
        }

        void Grow(int length)
        {
            var d = Data;
            Array.Resize(ref d, length);
            Data = d;
        }

        void Touch(MapObject o)
        {
            if (journal != null && !journal.Before.ContainsKey(o)) journal.Before[o] = ObjectEdit.State.Of(o);
        }

        void TouchPages(int chunk)
        {
            if (journal != null && !journal.PagesBefore.ContainsKey(chunk)) journal.PagesBefore[chunk] = chunkPages[chunk].ToArray();
        }

        // ---- edits

        /// <summary>Starts collecting changes into one undoable edit.</summary>
        public void BeginEdit(string description)
        {
            if (journal != null) throw new InvalidOperationException("An edit is already open");
            journal = new ObjectEdit { Description = description, OldLength = Data.Length };
        }

        /// <summary>Ends the edit begun with <see cref="BeginEdit"/>; null if nothing changed.</summary>
        public ObjectEdit EndEdit()
        {
            var e = journal ?? throw new InvalidOperationException("No edit is open");
            journal = null;
            e.NewLength = Data.Length;
            foreach (var o in e.Before.Keys) e.After[o] = ObjectEdit.State.Of(o);
            foreach (int c in e.PagesBefore.Keys) e.PagesAfter[c] = chunkPages[c].ToArray();
            return e.Writes.Count == 0 && e.OldLength == e.NewLength && e.Before.Count == 0 ? null : e;
        }

        public void Undo(ObjectEdit e)
        {
            if (journal != null) throw new InvalidOperationException("An edit is open");
            for (int i = e.Writes.Count - 1; i >= 0; i--) e.Writes[i].Old.CopyTo(Data.AsSpan(e.Writes[i].At));
            if (Data.Length != e.OldLength) Grow(e.OldLength);
            Restore(e.Before, e.PagesBefore);
        }

        public void Redo(ObjectEdit e)
        {
            if (journal != null) throw new InvalidOperationException("An edit is open");
            if (Data.Length != e.NewLength) Grow(e.NewLength);
            foreach (var w in e.Writes) w.New.CopyTo(Data.AsSpan(w.At));
            Restore(e.After, e.PagesAfter);
        }

        void Restore(Dictionary<MapObject, ObjectEdit.State> states, Dictionary<int, int[]> pages)
        {
            foreach (var kv in pages) { chunkPages[kv.Key].Clear(); chunkPages[kv.Key].AddRange(kv.Value); }
            foreach (var kv in states)
            {
                var o = kv.Key;
                if (byOffset.TryGetValue(o.FileOffset, out var at) && at == o) byOffset.Remove(o.FileOffset);
            }
            foreach (var kv in states)
            {
                var o = kv.Key;
                bool was = !o.Removed;
                kv.Value.ApplyTo(o);
                if (!o.Removed) byOffset[o.FileOffset] = o;
                if (was && o.Removed) Objects.Remove(o);
                else if (!was && !o.Removed) Objects.Add(o);
            }
        }

        internal void Move(MapObject o, int mapX, int mapY, int z)
        {
            if (o.Removed) throw new InvalidOperationException("The object was removed");
            if (mapX < 0 || mapY < 0 || mapX >= Width * ChunkUnits || mapY >= Height * ChunkUnits) throw new ArgumentOutOfRangeException(nameof(mapX), "Outside the map");
            if ((uint)z > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(z));
            int chunk = mapX / ChunkUnits + mapY / ChunkUnits * Width;
            int x = mapX % ChunkUnits, y = mapY % ChunkUnits, sub = x / SubchunkUnits + y / SubchunkUnits * 4;
            Touch(o);
            if (chunk != o.Chunk)
            {
                CheckMovable(o);
                int rec = CopyInto(chunk, o.Raw, o.PropertyOffset);
                Unlink(o);
                FreeRecord(o);
                byOffset.Remove(o.FileOffset);
                o.FileOffset = rec;
                o.ChunkX = chunk % Width * ChunkUnits;
                o.ChunkY = chunk / Width * ChunkUnits;
                byOffset[rec] = o;
                LinkHead(chunk, sub, o);
            }
            else if (sub != o.Subchunk)
            {
                Unlink(o);
                LinkHead(chunk, sub, o);
            }
            o.Subchunk = sub;
            PutU16(o.FileOffset + 4, x);
            PutU16(o.FileOffset + 6, y);
            PutU16(o.FileOffset + 8, z);
        }

        internal void Rotate(MapObject o, Quaternion q)
        {
            if (o.Removed) throw new InvalidOperationException("The object was removed");
            q = q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(q);
            if (q.W < 0) q = -q;
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt16LittleEndian(b, Q(q.X));
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(2), Q(q.Y));
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(4), Q(q.Z));
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(6), Q(q.W));
            Put(o.FileOffset + 0x0C, b);
        }

        static short Q(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), -32767, 32767);

        /// <summary>Deletes an object: unlinks it and gives back its space (undoable inside an edit).</summary>
        public void Remove(MapObject o)
        {
            if (o.Owner != this || o.Removed) return;
            Touch(o);
            Unlink(o);
            FreeRecord(o);
            byOffset.Remove(o.FileOffset);
            o.Removed = true;
            Objects.Remove(o);
        }

        /// <summary>A copy of an object (and its property segment) at a position across the map, in object units.</summary>
        public MapObject Duplicate(MapObject o, int mapX, int mapY, int z)
        {
            if (o.Removed) throw new InvalidOperationException("The object was removed");
            if (mapX < 0 || mapY < 0 || mapX >= Width * ChunkUnits || mapY >= Height * ChunkUnits) throw new ArgumentOutOfRangeException(nameof(mapX), "Outside the map");
            CheckMovable(o);
            int chunk = mapX / ChunkUnits + mapY / ChunkUnits * Width;
            int x = mapX % ChunkUnits, y = mapY % ChunkUnits;
            int rec = CopyInto(chunk, o.Raw, o.PropertyOffset);
            var n = new MapObject
            {
                Owner = this, FileOffset = rec, ChunkX = chunk % Width * ChunkUnits, ChunkY = chunk / Width * ChunkUnits,
                Subchunk = x / SubchunkUnits + y / SubchunkUnits * 4, Removed = true,
            };
            Touch(n);           // it did not exist before the edit
            n.Removed = false;
            byOffset[rec] = n;
            Objects.Add(n);
            LinkHead(chunk, n.Subchunk, n);
            PutU16(rec + 4, x);
            PutU16(rec + 6, y);
            PutU16(rec + 8, z);
            return n;
        }

        /// <summary>
        /// Whether an object's record can be copied to another chunk: a property segment that chains on to another
        /// (property 0xFF) is not followed, so such objects stay in their chunk.
        /// </summary>
        public bool CanLeaveChunk(MapObject o)
        {
            int p = o.PropertyOffset;
            if (p == 0 || DataStart + p + 16 > Data.Length) return true;
            var s = Data.AsSpan(DataStart + p, 4);
            return s[1] != 0xFF && s[2] != 0xFF && s[3] != 0xFF;
        }

        void CheckMovable(MapObject o)
        {
            if (!CanLeaveChunk(o)) throw new InvalidOperationException("The object's properties chain on to another segment; it can only move inside its chunk");
        }

        /// <summary>Writes a record (and a copy of its property segment) into a chunk's pages, unlinked; returns its file offset.</summary>
        int CopyInto(int chunk, byte[] raw, int propertyOffset)
        {
            int rec = Allocate(chunk, RecordSize);
            CountRecords(rec, +1);
            raw = (byte[])raw.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(raw, 0);
            if (Kind == ObjectFileKind.Nonfixed && propertyOffset != 0 && DataStart + propertyOffset + 16 <= Data.Length)
            {
                var seg = Data.AsSpan(DataStart + propertyOffset, 16).ToArray();
                int p = Allocate(chunk, 16);
                Put(p, seg);
                CountProperties(p, +1);
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x1C), (uint)(p - DataStart));
            }
            Put(rec, raw);
            return rec;
        }

        /// <summary>Room for <paramref name="size"/> bytes in one of the chunk's pages (a new page if none has it); returns the file offset.</summary>
        int Allocate(int chunk, int size)
        {
            foreach (int page in chunkPages[chunk])
            {
                int ph = DataStart + page;
                uint free = U32(ph + 4);
                if (free >= page + PageHeader && free + size <= page + PageSize)
                {
                    PutU32(ph + 4, (uint)(free + size));
                    return DataStart + (int)free;
                }
            }
            int fresh = AddPage(chunk);
            PutU32(DataStart + fresh + 4, (uint)(fresh + PageHeader + size));
            return DataStart + fresh + PageHeader;
        }

        /// <summary>Adds a page to the end of a chunk's chain: the first page of the free list, else one more at the file's end.</summary>
        int AddPage(int chunk)
        {
            TouchPages(chunk);
            int page;
            uint free = U32(FreePagesAt);
            int f = (int)(free & ~1u);
            if (free != 0 && (f & (PageSize - 1)) == 0 && DataStart + (long)f + PageSize <= Data.Length && !InUse(f))
            {
                page = f;
                PutU32(FreePagesAt, U32(DataStart + page));
            }
            else
            {
                int used = Data.Length - DataStart;
                page = (used + PageSize - 1) / PageSize * PageSize;
                Grow(DataStart + page + PageSize);
                PutU32(DataSizeAt, (uint)(page + PageSize));
            }
            int ph = DataStart + page;
            Put(ph, new byte[PageHeader]);
            PutU32(ph + 4, (uint)(page + PageHeader));
            PutU32(ph + 0x0C, (uint)(chunk % Width * ChunkUnits));
            PutU32(ph + 0x10, (uint)(chunk / Width * ChunkUnits));
            var pages = chunkPages[chunk];
            if (pages.Count == 0) PutU32(TableStart + 4 * chunk, (uint)page | 1);
            else PutU32(DataStart + pages[pages.Count - 1], (uint)page | 1);
            pages.Add(page);
            return page;
        }

        bool InUse(int page)
        {
            foreach (var l in chunkPages) if (l.Contains(page)) return true;
            return false;
        }

        void CountRecords(int fileOffset, int delta)
        {
            int ph = DataStart + ((fileOffset - DataStart) & ~(PageSize - 1));
            uint n = U32(ph + 0x14);
            if (delta > 0 || n > 0) PutU32(ph + 0x14, (uint)(n + delta));
        }

        void CountProperties(int fileOffset, int delta)
        {
            if (Kind != ObjectFileKind.Nonfixed) return;
            int ph = DataStart + ((fileOffset - DataStart) & ~(PageSize - 1));
            uint n = U32(ph + 0x18);
            if (delta > 0 || n > 0) PutU32(ph + 0x18, (uint)(n + delta));
        }

        /// <summary>
        /// Gives back an unlinked record's space. Fixed pages are packed, so the page's last record moves into the
        /// hole; nonfixed pages keep holes (as the game's own files do), and only the counts drop.
        /// </summary>
        void FreeRecord(MapObject o)
        {
            int rec = o.FileOffset, rel = rec - DataStart;
            int page = rel & ~(PageSize - 1), ph = DataStart + page;
            CountRecords(rec, -1);
            if (Kind == ObjectFileKind.Nonfixed)
            {
                int p = o.PropertyOffset;
                if (p != 0 && DataStart + p + 16 <= Data.Length) CountProperties(DataStart + p, -1);
                return;
            }
            int free = (int)U32(ph + 4), last = free - RecordSize;
            if (last == rel) { PutU32(ph + 4, (uint)last); return; }
            if (last < page + PageHeader || !byOffset.TryGetValue(DataStart + last, out var tail) || tail.Removed) return;
            int slot = LinkSlot(tail);
            if (slot < 0) return;
            Touch(tail);
            Put(rec, Data.AsSpan(DataStart + last, RecordSize).ToArray());
            PutU32(slot, (uint)rel);
            byOffset.Remove(tail.FileOffset);
            tail.FileOffset = rec;
            byOffset[rec] = tail;
            PutU32(ph + 4, (uint)last);
        }

        /// <summary>The file offset of the u32 that points at the object (a list head or the previous record), or -1.</summary>
        int LinkSlot(MapObject o)
        {
            var pages = chunkPages[o.Chunk];
            if (pages.Count == 0) return -1;
            int slot = DataStart + pages[0] + 0x20 + 4 * o.Subchunk, target = o.FileOffset - DataStart;
            for (int guard = 0; guard < 100000; guard++)
            {
                uint v = U32(slot);
                if (v == 0) return -1;
                if ((int)(v & ~1u) == target) return slot;
                long next = DataStart + (long)(v & ~1u);
                if (next + 4 > Data.Length) return -1;
                slot = (int)next;
            }
            return -1;
        }

        void Unlink(MapObject o)
        {
            int slot = LinkSlot(o);
            if (slot < 0) throw new InvalidDataException($"Object at 0x{o.FileOffset:X} is not in its subchunk's list");
            PutU32(slot, U32(o.FileOffset));
            PutU32(o.FileOffset, 0);
        }

        void LinkHead(int chunk, int sub, MapObject o)
        {
            int head = DataStart + chunkPages[chunk][0] + 0x20 + 4 * sub;
            PutU32(o.FileOffset, U32(head));
            PutU32(head, (uint)(o.FileOffset - DataStart));
        }
    }

    /// <summary>One undoable change to an <see cref="ObjectMap"/>: the bytes written, and the objects' and chunks' state before and after.</summary>
    public sealed class ObjectEdit
    {
        public string Description { get; internal set; }
        internal readonly List<(int At, byte[] Old, byte[] New)> Writes = new List<(int, byte[], byte[])>();
        internal int OldLength, NewLength;
        internal readonly Dictionary<MapObject, State> Before = new Dictionary<MapObject, State>(), After = new Dictionary<MapObject, State>();
        internal readonly Dictionary<int, int[]> PagesBefore = new Dictionary<int, int[]>(), PagesAfter = new Dictionary<int, int[]>();
        /// <summary>The objects the edit touched (including ones it moved to make room).</summary>
        public IEnumerable<MapObject> Objects => Before.Keys;

        internal readonly struct State
        {
            readonly int offset, chunkX, chunkY, sub;
            readonly bool removed;
            State(MapObject o) { offset = o.FileOffset; chunkX = o.ChunkX; chunkY = o.ChunkY; sub = o.Subchunk; removed = o.Removed; }
            public static State Of(MapObject o) => new State(o);
            public void ApplyTo(MapObject o) { o.FileOffset = offset; o.ChunkX = chunkX; o.ChunkY = chunkY; o.Subchunk = sub; o.Removed = removed; }
        }
    }
}
