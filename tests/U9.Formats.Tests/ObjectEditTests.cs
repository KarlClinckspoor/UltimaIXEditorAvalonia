using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using U9.Formats.Objects;
using Xunit;

namespace U9.Formats.Tests
{
    public class ObjectEditTests
    {
        static byte[] Bytes(string relative) => File.ReadAllBytes(GameData.PathOf(relative));
        static readonly string Fixed9 = Path.Combine("static", "fixed.9"), Nonfixed9 = Path.Combine("runtime", "nonfixed.9");

        /// <summary>
        /// Walks a file as the game would: per chunk, the records in its first page's 16 lists. Returns per chunk the
        /// list length minus the records the pages count, and fails on a record filed under the wrong subchunk, a
        /// record outside the chunk's pages, or a free offset outside its page.
        /// </summary>
        static Dictionary<int, int> Check(ObjectMap m)
        {
            var d = m.Data;
            int ds = m.DataStart;
            var balance = new Dictionary<int, int>();
            for (int cy = 0; cy < m.Height; cy++)
                for (int cx = 0; cx < m.Width; cx++)
                {
                    var pages = m.PagesOf(cx, cy);
                    if (pages.Count == 0) continue;
                    int listed = 0, counted = 0;
                    foreach (int pg in pages)
                    {
                        counted += (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(ds + pg + 0x14));
                        uint free = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(ds + pg + 4));
                        if (free != 0) Assert.InRange((long)free, pg + 0x60, pg + 4096);     // one shipped page has 0
                    }
                    var seen = new HashSet<uint>();
                    for (int s = 0; s < 16; s++)
                    {
                        uint p = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(ds + pages[0] + 0x20 + 4 * s));
                        while (p != 0 && seen.Add(p))
                        {
                            int at = ds + (int)(p & ~1u);
                            int x = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 4)), y = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 6));
                            if (x > 4095 || y > 4095) break;      // the shipped lists that run into property data
                            Assert.Equal(s, x / 1024 + y / 1024 * 4);
                            listed++;
                            p = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
                        }
                    }
                    balance[cx + cy * m.Width] = listed - counted;
                }
            return balance;
        }

        static void AssertSameBalance(Dictionary<int, int> before, Dictionary<int, int> after)
        {
            foreach (var kv in after) Assert.Equal(before.TryGetValue(kv.Key, out int b) ? b : 0, kv.Value);
        }

        static string Key(MapObject o) => $"{o.MapX:D6} {o.MapY:D6} {o.Z} {o.Type} {o.Rotation} {o.Flags}";

        [Fact]
        public void ShippedFilesPassTheCheck()
        {
            GameData.Require();
            var f = Check(ObjectMap.Read(Bytes(Fixed9), ObjectFileKind.Fixed));
            Assert.All(f.Values, v => Assert.Equal(0, v));
            var n = Check(ObjectMap.Read(Bytes(Nonfixed9), ObjectFileKind.Nonfixed));
            Assert.True(n.Values.Count(v => v != 0) <= 20);
        }

        [Fact]
        public void MovingInsideASubchunkChangesOnlyThePosition()
        {
            GameData.Require();
            byte[] original = Bytes(Fixed9);
            var m = ObjectMap.Read((byte[])original.Clone(), ObjectFileKind.Fixed);
            var o = m.Objects.First(x => x.X % 1024 < 900 && x.Y % 1024 < 900);
            int offset = o.FileOffset;
            o.MoveTo(o.MapX + 50, o.MapY + 60, o.Z + 70);
            var back = ObjectMap.Read(m.ToBytes(), ObjectFileKind.Fixed);
            var b = back.Objects.Single(x => x.FileOffset == offset);
            Assert.Equal(Key(o), Key(b));
            int diff = Enumerable.Range(0, original.Length).Count(i => original[i] != m.Data[i]);
            Assert.InRange(diff, 1, 6);
        }

        public static TheoryData<string, bool> Files() => new TheoryData<string, bool> { { Fixed9, true }, { Nonfixed9, false } };

        [Theory]
        [MemberData(nameof(Files))]
        public void MovingAcrossSubchunksAndChunksKeepsTheFileSound(string relative, bool isFixed)
        {
            GameData.Require();
            var kind = isFixed ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;
            byte[] original = Bytes(relative);
            var m = ObjectMap.Read((byte[])original.Clone(), kind);
            var before = Check(m);
            int count = m.Objects.Count;

            // one object into the next subchunk, one into the next chunk east, one with properties into a chunk north
            var a = m.Objects.First(x => x.X < 3000);
            var b = m.Objects.First(x => x != a && x.MapX + 4096 < m.Width * 4096 && m.CanLeaveChunk(x));
            var c = isFixed ? m.Objects.First(x => x != a && x != b && x.MapY + 4096 < m.Height * 4096)
                            : m.Objects.First(x => x != a && x != b && x.Properties != null && m.CanLeaveChunk(x) && x.MapY + 4096 < m.Height * 4096);
            byte[] props = c.Properties != null ? m.Data.AsSpan(m.DataStart + c.PropertyOffset, 16).ToArray() : null;
            var others = m.Objects.Where(x => x != a && x != b && x != c).Select(Key).ToHashSet();

            a.MoveTo(a.MapX + 1024, a.MapY, a.Z);
            b.MoveTo(b.MapX + 4096, b.MapY, b.Z + 4);
            c.MoveTo(c.MapX, c.MapY + 4096, c.Z);
            var expect = new[] { Key(a), Key(b), Key(c) };

            var back = ObjectMap.Read(m.ToBytes(), kind);
            Assert.Equal(count, back.Objects.Count);
            AssertSameBalance(before, Check(back));
            var keys = back.Objects.Select(Key).ToHashSet();
            Assert.All(expect, k => Assert.Contains(k, keys));
            Assert.True(others.IsSubsetOf(keys));
            if (props != null)
            {
                var moved = back.Objects.Single(x => Key(x) == expect[2]);
                Assert.Equal(props, back.Data.AsSpan(back.DataStart + moved.PropertyOffset, 16).ToArray());
                Assert.Equal(c.ChunkY, moved.ChunkY);
            }
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void UndoRestoresTheBytesAndRedoRepeatsThem(string relative, bool isFixed)
        {
            GameData.Require();
            var kind = isFixed ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;
            byte[] original = Bytes(relative);
            var m = ObjectMap.Read((byte[])original.Clone(), kind);
            var keys = m.Objects.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var o = m.Objects.First(x => m.CanLeaveChunk(x) && x.MapX + 8192 < m.Width * 4096);

            m.BeginEdit("move");
            o.MoveTo(o.MapX + 8192, o.MapY, o.Z);
            o.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f));
            m.Remove(m.Objects.First(x => x != o));
            var copy = m.Duplicate(o, o.MapX - 100, o.MapY + 100, o.Z);
            var edit = m.EndEdit();
            Assert.NotNull(edit);
            byte[] edited = m.ToBytes();
            var editedKeys = m.Objects.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

            m.Undo(edit);
            Assert.Equal(original, m.Data);
            Assert.Equal(keys, m.Objects.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList());
            Assert.True(copy.Removed);

            m.Redo(edit);
            Assert.Equal(edited, m.Data);
            Assert.Equal(editedKeys, m.Objects.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList());
            Assert.Equal(editedKeys, ObjectMap.Read(m.ToBytes(), kind).Objects.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList());
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void AFullChunkGetsAnotherPage(string relative, bool isFixed)
        {
            GameData.Require();
            var kind = isFixed ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;
            var m = ObjectMap.Read(Bytes(relative), kind);
            var before = Check(m);
            var o = m.Objects.First(x => x.Properties == null);
            int pages = m.PagesOf(o.ChunkX / 4096, o.ChunkY / 4096).Count;
            int count = m.Objects.Count;
            for (int i = 0; i < 4096 / m.RecordSize + 5; i++) m.Duplicate(o, o.ChunkX + i % 4096, o.ChunkY + i * 7 % 4096, o.Z);
            Assert.True(m.PagesOf(o.ChunkX / 4096, o.ChunkY / 4096).Count > pages);
            var back = ObjectMap.Read(m.ToBytes(), kind);
            Assert.Equal(m.Objects.Count, back.Objects.Count);
            Assert.Equal(count + 4096 / m.RecordSize + 5, back.Objects.Count);
            AssertSameBalance(before, Check(back));
        }

        [Fact]
        public void RemovingAFixedObjectKeepsThePagePacked()
        {
            GameData.Require();
            var m = ObjectMap.Read(Bytes(Fixed9), ObjectFileKind.Fixed);
            var before = Check(m);
            var victim = m.Objects[500];
            var rest = m.Objects.Where(x => x != victim).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            m.Remove(victim);
            var back = ObjectMap.Read(m.ToBytes(), ObjectFileKind.Fixed);
            Assert.Equal(rest, back.Objects.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList());
            AssertSameBalance(before, Check(back));
        }
    }
}
