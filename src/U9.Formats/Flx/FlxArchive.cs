using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace U9.Formats.Flx
{
    /// <summary>How <see cref="FlxArchive.Save(Stream, FlxLayout)"/> places records in the file.</summary>
    public enum FlxLayout
    {
        /// <summary>
        /// Keep every record at its original offset when it still fits, keep the bytes between records
        /// (the original tools left stale data there), and append records that grew or are new at the end.
        /// An unchanged archive comes out byte for byte the same, and an edit changes as few bytes as possible.
        /// </summary>
        Preserve,
        /// <summary>Write the records one after another in index order, with no gaps.</summary>
        Compact,
    }

    /// <summary>
    /// An Origin FLX archive as used by Ultima IX (*.flx, texture8.N, texture16.N).
    /// <para>Layout (all little-endian):</para>
    /// <list type="bullet">
    /// <item>0x00, 0x4C bytes: comment, blank (spaces) in every shipped file.</item>
    /// <item>0x4C, u32: unknown, 0 in every shipped file.</item>
    /// <item>0x50, u32: number of directory slots.</item>
    /// <item>0x54, u32: unknown, 2 in every shipped file (probably a version).</item>
    /// <item>0x58, u32 and 0x5C, u32: both the total file size.</item>
    /// <item>0x60, 0x20 bytes: unknown, the u32 at 0x68 is 1 in every shipped file.</item>
    /// <item>0x80: directory, one (u32 offset, u32 length) per slot. Offset 0 means the slot is empty.</item>
    /// </list>
    /// Records are read on demand from the source file, so the 300 MB bitmap archives open instantly.
    /// The archive keeps the source file open until it is disposed.
    /// </summary>
    public sealed class FlxArchive : IDisposable
    {
        public const int HeaderSize = 0x80;
        public const int CommentSize = 0x4C;
        public const int DirectoryEntrySize = 8;

        sealed class Slot
        {
            public long SourceOffset;   // 0 = none in the source
            public int SourceLength;
            public byte[] Replaced;     // set when the record was changed or added
            public bool Removed;
        }

        readonly List<Slot> slots = new List<Slot>();
        readonly List<(long Offset, byte[] Data)> holes = new List<(long, byte[])>();
        Stream source;
        long sourceDataEnd;
        int sourceSlotCount;

        /// <summary>The 0x4C-byte comment field, kept as it is.</summary>
        public byte[] Comment { get; set; } = NewComment();
        public uint Unknown4C { get; set; }
        public uint Version { get; set; } = 2;
        /// <summary>The 0x20 bytes at 0x60, kept as they are.</summary>
        public byte[] Reserved60 { get; set; } = NewReserved();

        /// <summary>Number of directory slots, used or empty.</summary>
        public int Count => slots.Count;

        public FlxArchive() { }

        static byte[] NewComment() { var c = new byte[CommentSize]; c.AsSpan().Fill((byte)' '); return c; }
        static byte[] NewReserved() { var r = new byte[0x20]; r[8] = 1; return r; }

        /// <summary>Opens an archive file for reading. Records are loaded when asked for.</summary>
        public static FlxArchive Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            try { return Open(fs); }
            catch { fs.Dispose(); throw; }
        }

        /// <summary>Reads an archive from a seekable stream, which the archive then owns.</summary>
        public static FlxArchive Open(Stream stream)
        {
            if (!stream.CanSeek) throw new ArgumentException("FLX needs a seekable stream", nameof(stream));
            long length = stream.Length;
            if (length < HeaderSize) throw new InvalidDataException("File is shorter than an FLX header");

            var head = new byte[HeaderSize];
            stream.Position = 0;
            stream.ReadExactly(head);

            var a = new FlxArchive();
            a.Comment = head.AsSpan(0, CommentSize).ToArray();
            a.Unknown4C = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x4C));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x50));
            a.Version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x54));
            a.Reserved60 = head.AsSpan(0x60, 0x20).ToArray();

            long dirEnd = HeaderSize + (long)count * DirectoryEntrySize;
            if (dirEnd > length) throw new InvalidDataException($"FLX directory of {count} slots runs past the end of the file");

            var dir = new byte[count * DirectoryEntrySize];
            stream.ReadExactly(dir);
            var used = new List<(long Off, long End)>();
            for (int i = 0; i < count; i++)
            {
                uint off = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i * 8));
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i * 8 + 4));
                var s = new Slot();
                if (off != 0)
                {
                    if (off < dirEnd || off + (long)len > length)
                        throw new InvalidDataException($"FLX record {i} (offset {off}, length {len}) lies outside the data area");
                    s.SourceOffset = off;
                    s.SourceLength = (int)len;
                    used.Add((off, off + len));
                }
                a.slots.Add(s);
            }

            // Everything in the data area not covered by a record is kept as a hole, so it can be written back.
            used.Sort((x, y) => x.Off.CompareTo(y.Off));
            long pos = dirEnd;
            foreach (var u in used)
            {
                if (u.Off > pos) a.holes.Add((pos, ReadAt(stream, pos, (int)(u.Off - pos))));
                pos = Math.Max(pos, u.End);
            }
            if (length > pos) a.holes.Add((pos, ReadAt(stream, pos, (int)(length - pos))));

            a.source = stream;
            a.sourceDataEnd = length;
            a.sourceSlotCount = (int)count;
            return a;
        }

        static byte[] ReadAt(Stream s, long offset, int length)
        {
            var b = new byte[length];
            s.Position = offset;
            s.ReadExactly(b);
            return b;
        }

        /// <summary>True when slot <paramref name="index"/> holds a record.</summary>
        public bool Has(int index)
        {
            var s = slots[index];
            if (s.Removed) return false;
            return s.Replaced != null || s.SourceOffset != 0;
        }

        /// <summary>Length of the record in slot <paramref name="index"/>, 0 if empty.</summary>
        public int LengthOf(int index)
        {
            var s = slots[index];
            if (s.Removed) return 0;
            return s.Replaced != null ? s.Replaced.Length : s.SourceLength;
        }

        /// <summary>The record in slot <paramref name="index"/>, or null if the slot is empty. Returns a fresh copy.</summary>
        public byte[] Read(int index)
        {
            var s = slots[index];
            if (s.Removed) return null;
            if (s.Replaced != null) return (byte[])s.Replaced.Clone();
            if (s.SourceOffset == 0) return null;
            lock (slots) return ReadAt(source, s.SourceOffset, s.SourceLength);
        }

        /// <summary>Replaces the record in slot <paramref name="index"/>; null empties the slot.</summary>
        public void Write(int index, byte[] data)
        {
            var s = slots[index];
            if (data == null) { s.Replaced = null; s.Removed = true; return; }
            s.Replaced = (byte[])data.Clone();
            s.Removed = false;
        }

        /// <summary>Changes the number of directory slots. Growing adds empty slots; shrinking drops the last ones.</summary>
        public void Resize(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            while (slots.Count > count) slots.RemoveAt(slots.Count - 1);
            while (slots.Count < count) slots.Add(new Slot());
        }

        /// <summary>Indices of all slots that hold a record.</summary>
        public IEnumerable<int> UsedIndices()
        {
            for (int i = 0; i < slots.Count; i++) if (Has(i)) yield return i;
        }

        /// <summary>Writes the archive to a file. Saving over the source file goes through a temporary file.</summary>
        public void Save(string path, FlxLayout layout = FlxLayout.Preserve)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                Save(fs, layout);
            bool overSource = source is FileStream src && string.Equals(Path.GetFullPath(src.Name), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (overSource)
            {
                // Pull every record we still need into memory before the source file goes away.
                for (int i = 0; i < slots.Count; i++)
                    if (slots[i].Replaced == null && !slots[i].Removed && slots[i].SourceOffset != 0) slots[i].Replaced = Read(i);
                source.Dispose();
                source = null;
            }
            File.Move(tmp, path, true);
        }

        /// <summary>Writes the archive to a stream. The stream does not need to be seekable.</summary>
        public void Save(Stream output, FlxLayout layout = FlxLayout.Preserve)
        {
            int count = slots.Count;
            long dirEnd = HeaderSize + (long)count * DirectoryEntrySize;
            bool preserve = layout == FlxLayout.Preserve && count == sourceSlotCount && source != null;

            var offsets = new long[count];
            var regions = new List<(long Offset, int Index, byte[] Hole)>();
            long end = dirEnd;

            if (preserve)
            {
                var pending = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    var s = slots[i];
                    if (!Has(i)) continue;
                    if (s.SourceOffset != 0 && LengthOf(i) <= s.SourceLength) offsets[i] = s.SourceOffset;
                    else pending.Add(i);
                }
                foreach (var h in holes) regions.Add((h.Offset, -1, h.Data));
                end = Math.Max(dirEnd, sourceDataEnd);
                foreach (int i in pending) { offsets[i] = end; end += LengthOf(i); }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    if (!Has(i)) continue;
                    offsets[i] = end;
                    end += LengthOf(i);
                }
            }
            for (int i = 0; i < count; i++) if (Has(i)) regions.Add((offsets[i], i, null));
            foreach (var r in regions)
                end = Math.Max(end, r.Offset + (r.Hole != null ? r.Hole.Length : LengthOf(r.Index)));
            if (end > uint.MaxValue) throw new InvalidOperationException("FLX archive would be larger than 4 GB");

            var head = new byte[HeaderSize];
            Comment.AsSpan(0, Math.Min(CommentSize, Comment.Length)).CopyTo(head);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x4C), Unknown4C);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x50), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x54), Version);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x58), (uint)end);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x5C), (uint)end);
            Reserved60.AsSpan(0, Math.Min(0x20, Reserved60.Length)).CopyTo(head.AsSpan(0x60));
            output.Write(head);

            var dir = new byte[count * DirectoryEntrySize];
            for (int i = 0; i < count; i++)
            {
                if (!Has(i)) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(i * 8), (uint)offsets[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(i * 8 + 4), (uint)LengthOf(i));
            }
            output.Write(dir);

            regions.Sort((x, y) => x.Offset.CompareTo(y.Offset));
            long pos = dirEnd;
            foreach (var r in regions)
            {
                if (r.Offset < pos)
                    throw new InvalidOperationException($"FLX layout overlaps at offset {r.Offset}");
                WriteZeros(output, r.Offset - pos);
                byte[] data = r.Hole ?? Read(r.Index);
                output.Write(data);
                pos = r.Offset + data.Length;
            }
            WriteZeros(output, end - pos);
        }

        static void WriteZeros(Stream s, long n)
        {
            if (n <= 0) return;
            var z = new byte[(int)Math.Min(n, 1 << 16)];
            while (n > 0) { int k = (int)Math.Min(n, z.Length); s.Write(z, 0, k); n -= k; }
        }

        public void Dispose()
        {
            source?.Dispose();
            source = null;
        }
    }
}
