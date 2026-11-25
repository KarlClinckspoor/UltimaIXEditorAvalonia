using System;
using System.Collections.Generic;
using System.IO;

namespace U9.Mods
{
    /// <summary>
    /// The difference between an original file and an edited one (docs/mod-format.md, "The delta"): COPY runs of the
    /// original and ADD the new bytes. A delta applies to one exact original; reading outside it, or giving a result of
    /// another length, is refused. Ported from the Underworld editor's format 2, without its archive BLOCK op.
    /// </summary>
    public static class Delta
    {
        const byte OpEnd = 0x00, OpCopy = 0x01, OpAdd = 0x02;
        static readonly byte[] Magic = { (byte)'U', (byte)'9', (byte)'D', (byte)'1' };

        /// <summary>Hashed window: a COPY found through the index matches at least this many bytes.</summary>
        const int Window = 16;
        /// <summary>A COPY that continues the previous one's displacement (bytes changed in place) needs only this many.</summary>
        const int AlignedMin = 4;
        /// <summary>A COPY found elsewhere needs this many (shorter runs go in as ADD).</summary>
        const int MinCopy = 8;
        const int MaxCandidates = 64;
        /// <summary>Above this size only every <see cref="BigStride"/>th window of the original is indexed.</summary>
        const int BigFile = 16 << 20, BigStride = 16;

        // ---- making

        /// <summary>The delta from <paramref name="original"/> to <paramref name="edited"/>, checked: applying it gives <paramref name="edited"/>, or this throws.</summary>
        public static byte[] Make(byte[] original, byte[] edited)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (edited == null) throw new ArgumentNullException(nameof(edited));
            var w = new Writer();
            w.Bytes(Magic);
            w.U(original.Length);
            w.U(edited.Length);
            var index = new Index(original);
            long prevEnd = 0;
            Ops(index, edited, 0, edited.Length, w, ref prevEnd);
            w.Byte(OpEnd);
            var delta = w.ToArray();
            var check = Apply(original, delta);
            if (!check.AsSpan().SequenceEqual(edited)) throw new InvalidOperationException("The delta does not rebuild the edited file (a bug in Delta.Make).");
            return delta;
        }

        static void Ops(Index index, byte[] edited, int start, int end, Writer w, ref long prevEnd)
        {
            byte[] src = index.Source;
            int i = start, addStart = start;
            long alignSrc = prevEnd;
            int alignDst = start;
            while (i < end)
            {
                int bestLen = 0, bestBack = 0;
                long bestSrc = -1;
                long a = alignSrc + (i - alignDst);
                if (a >= 0 && a < src.Length)
                {
                    int l = MatchLength(src, (int)a, edited, i, end);
                    if (l >= AlignedMin) { bestLen = l; bestSrc = a; }
                }
                if (i + Window <= end && bestLen < 4 * Window)
                {
                    foreach (int c in index.Candidates(edited, i))
                    {
                        int l = MatchLength(src, c, edited, i, end);
                        if (l < Window) continue;
                        int back = 0;
                        while (i - back > addStart && c - back > 0 && edited[i - back - 1] == src[c - back - 1]) back++;
                        if (l + back > bestLen + bestBack) { bestLen = l; bestBack = back; bestSrc = c; }
                    }
                }
                if (bestSrc < 0 || (bestLen + bestBack < MinCopy && bestSrc != a)) { i++; continue; }
                int copyDst = i - bestBack;
                long copySrc = bestSrc - bestBack;
                int copyLen = bestLen + bestBack;
                if (copyDst > addStart)
                {
                    w.Byte(OpAdd);
                    w.U(copyDst - addStart);
                    w.Bytes(edited, addStart, copyDst - addStart);
                }
                w.Byte(OpCopy);
                w.Z(copySrc - prevEnd);
                w.U(copyLen);
                prevEnd = copySrc + copyLen;
                i = copyDst + copyLen;
                addStart = i;
                alignSrc = prevEnd;
                alignDst = i;
            }
            if (end > addStart)
            {
                w.Byte(OpAdd);
                w.U(end - addStart);
                w.Bytes(edited, addStart, end - addStart);
            }
        }

        static int MatchLength(byte[] src, int s, byte[] dst, int d, int end)
        {
            int max = Math.Min(src.Length - s, end - d);
            return max <= 0 ? 0 : src.AsSpan(s, max).CommonPrefixLength(dst.AsSpan(d, max));
        }

        /// <summary>The original's 16-byte windows by hash (every one, or every 16th in a big file).</summary>
        sealed class Index
        {
            public readonly byte[] Source;
            readonly int[] head, next;
            readonly int mask, stride;

            public Index(byte[] src)
            {
                Source = src;
                stride = src.Length > BigFile ? BigStride : 1;
                int windows = Math.Max(0, src.Length - Window + 1);
                int n = (windows + stride - 1) / stride;
                int bits = 10;
                while (bits < 24 && (1 << bits) < n) bits++;
                mask = (1 << bits) - 1;
                head = new int[1 << bits];
                Array.Fill(head, -1);
                next = new int[n];
                for (int k = 0; k < n; k++)
                {
                    int h = Hash(src, k * stride);
                    next[k] = head[h];
                    head[h] = k;
                }
            }

            int Hash(byte[] b, int p)
            {
                ulong x = BitConverter.ToUInt64(b, p), y = BitConverter.ToUInt64(b, p + 8);
                ulong h = x * 0x9E3779B97F4A7C15UL ^ (y + 0x632BE59BD9B4E019UL) * 0xC2B2AE3D27D4EB4FUL;
                return (int)(h >> 40) & mask;
            }

            public IEnumerable<int> Candidates(byte[] dst, int p)
            {
                if (next.Length == 0) yield break;
                int c = head[Hash(dst, p)], k = 0;
                while (c >= 0 && k++ < MaxCandidates) { yield return c * stride; c = next[c]; }
            }
        }

        // ---- applying

        /// <summary>The edited file: <paramref name="delta"/> applied to <paramref name="original"/>. Throws InvalidDataException when the delta is damaged or made from another file.</summary>
        public static byte[] Apply(byte[] original, byte[] delta)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            var r = new Reader(delta);
            for (int k = 0; k < Magic.Length; k++)
                if (r.Byte() != Magic[k]) throw new InvalidDataException("Not a delta (U9D1).");
            long baseLen = r.U(), targetLen = r.U();
            if (baseLen != original.Length) throw new InvalidDataException($"The delta was made from a file of {baseLen} bytes, not {original.Length}.");
            var output = new byte[targetLen];
            int pos = 0;
            long prevEnd = 0;
            while (true)
            {
                byte op = r.Byte();
                if (op == OpEnd) break;
                if (op == OpCopy)
                {
                    long off = prevEnd + r.Z(), len = r.U();
                    if (off < 0 || off + len > original.Length) throw new InvalidDataException("The delta copies from outside the original.");
                    if (pos + len > output.Length) throw new InvalidDataException("The delta writes past its promised length.");
                    Array.Copy(original, off, output, pos, len);
                    pos += (int)len;
                    prevEnd = off + len;
                }
                else if (op == OpAdd)
                {
                    long len = r.U();
                    if (pos + len > output.Length) throw new InvalidDataException("The delta writes past its promised length.");
                    r.Bytes(output, pos, (int)len);
                    pos += (int)len;
                }
                else throw new InvalidDataException($"Unknown delta op 0x{op:X2}.");
            }
            if (!r.AtEnd) throw new InvalidDataException("The delta has bytes after its end.");
            if (pos != output.Length) throw new InvalidDataException($"The delta gives {pos} bytes, not the {targetLen} it promises.");
            return output;
        }

        /// <summary>The bytes a delta adds (its ADD ops): what a mod carries of its own.</summary>
        public static List<byte[]> Added(byte[] delta)
        {
            var r = new Reader(delta);
            for (int k = 0; k < Magic.Length; k++)
                if (r.Byte() != Magic[k]) throw new InvalidDataException("Not a delta (U9D1).");
            r.U(); r.U();
            var list = new List<byte[]>();
            while (true)
            {
                byte op = r.Byte();
                if (op == OpEnd) return list;
                if (op == OpCopy) { r.Z(); r.U(); }
                else if (op == OpAdd) list.Add(r.Take((int)r.U()));
                else throw new InvalidDataException($"Unknown delta op 0x{op:X2}.");
            }
        }

        // ---- numbers

        sealed class Writer
        {
            readonly MemoryStream ms = new MemoryStream();
            public void Byte(byte b) => ms.WriteByte(b);
            public void Bytes(byte[] b) => ms.Write(b, 0, b.Length);
            public void Bytes(byte[] b, int off, int len) => ms.Write(b, off, len);
            public void U(long v)
            {
                if (v < 0) throw new ArgumentOutOfRangeException(nameof(v));
                UL((ulong)v);
            }
            /// <summary>Signed, zigzag-coded: 0, -1, 1, -2... as 0, 1, 2, 3...</summary>
            public void Z(long v) => UL(((ulong)v << 1) ^ (ulong)(v >> 63));
            void UL(ulong u)
            {
                while (u >= 0x80) { ms.WriteByte((byte)(u | 0x80)); u >>= 7; }
                ms.WriteByte((byte)u);
            }
            public byte[] ToArray() => ms.ToArray();
        }

        sealed class Reader
        {
            readonly byte[] b;
            int p;
            public Reader(byte[] data) { b = data; }
            public bool AtEnd => p == b.Length;
            public byte Byte()
            {
                if (p >= b.Length) throw new InvalidDataException("The delta ends early.");
                return b[p++];
            }
            public long U()
            {
                ulong v = UL();
                if (v > int.MaxValue) throw new InvalidDataException("A number in the delta is too large.");
                return (long)v;
            }
            public long Z()
            {
                ulong u = UL();
                long v = (long)(u >> 1) ^ -(long)(u & 1);
                if (v > int.MaxValue || v < -(long)int.MaxValue) throw new InvalidDataException("A number in the delta is too large.");
                return v;
            }
            ulong UL()
            {
                ulong v = 0;
                for (int shift = 0; ; shift += 7)
                {
                    if (shift > 63) throw new InvalidDataException("A number in the delta is too long.");
                    byte x = Byte();
                    v |= (ulong)(x & 0x7F) << shift;
                    if ((x & 0x80) == 0) return v;
                }
            }
            public void Bytes(byte[] dst, int off, int len)
            {
                if (len < 0 || p + len > b.Length) throw new InvalidDataException("The delta ends early.");
                Array.Copy(b, p, dst, off, len);
                p += len;
            }
            public byte[] Take(int len)
            {
                var d = new byte[len];
                Bytes(d, 0, len);
                return d;
            }
        }
    }
}
