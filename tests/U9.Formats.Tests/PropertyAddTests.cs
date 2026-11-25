using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using U9.Formats.Models;
using U9.Formats.Objects;
using Xunit;

namespace U9.Formats.Tests
{
    public class PropertyAddTests
    {
        static ObjectMap Nonfixed9() => ObjectMap.Load(GameData.PathOf(Path.Combine("runtime", "nonfixed.9")), ObjectFileKind.Nonfixed);

        static long CountedSegments(ObjectMap m)
        {
            long n = 0;
            for (int cy = 0; cy < m.Height; cy++)
                for (int cx = 0; cx < m.Width; cx++)
                    foreach (int p in m.PagesOf(cx, cy)) n += BinaryPrimitives.ReadUInt32LittleEndian(m.Data.AsSpan(m.DataStart + p + 0x18));
            return n;
        }

        static long ReachedSegments(ObjectMap m) =>
            m.Objects.Sum(o => { var t = m.TreeOf(o); return (long)t.Segments.Count + t.Inside.Sum(i => i.Segments.Count); });

        static (int, int, int)[] Props(ObjectMap m, MapObject o) => m.PropertiesOf(o).Select(p => (p.Id, p.A, p.B)).ToArray();

        [Fact]
        public void PropertiesAddIntoFreePlacesThenChainAndComeOffAgain()
        {
            var m = Nonfixed9();
            var bare = m.Objects.First(o => o.PropertyOffset == 0 && m.CanLeaveChunk(o));
            var before = m.ToBytes();
            long counted = CountedSegments(m);
            Assert.Equal(counted, ReachedSegments(m));

            m.BeginEdit("add");
            m.AddProperty(bare, 0x76, 15, 35);          // a new segment
            m.AddProperty(bare, 0x77, 5, 9);            // its second place
            m.AddProperty(bare, 0x10, 120, 0);          // its third
            m.AddProperty(bare, 0x14, 3, 0);            // full: chains on
            var edit = m.EndEdit();
            var want = new[] { (0x76, 15, 35), (0x77, 5, 9), (0x10, 120, 0), (0x14, 3, 0) };
            Assert.Equal(want.OrderBy(p => p.Item1), Props(m, bare).OrderBy(p => p.Item1));
            Assert.Equal(2, m.TreeOf(bare).Segments.Count);
            Assert.False(m.CanLeaveChunk(bare));
            Assert.Equal(counted + 2, CountedSegments(m));
            Assert.Throws<InvalidOperationException>(() => m.AddProperty(bare, 0x76, 1, 2));

            var reread = ObjectMap.Read(m.ToBytes(), ObjectFileKind.Nonfixed);
            var again = reread.Objects.Single(o => o.FileOffset == bare.FileOffset);
            Assert.Equal(want.OrderBy(p => p.Item1), Props(reread, again).OrderBy(p => p.Item1));
            Assert.Equal(CountedSegments(reread), ReachedSegments(reread));

            m.BeginEdit("remove");
            m.RemoveProperty(bare, m.PropertiesOf(bare).Single(p => p.Id == 0x77));
            var removal = m.EndEdit();
            Assert.Equal(new[] { (0x76, 15, 35), (0x10, 120, 0), (0x14, 3, 0) }.OrderBy(p => p.Item1), Props(m, bare).OrderBy(p => p.Item1));

            m.Undo(removal);
            m.Undo(edit);
            Assert.Equal(before, m.Data);
        }

        [Fact]
        public void RemovingTheOnlyPropertyLeavesNoSegment()
        {
            var m = Nonfixed9();
            var bare = m.Objects.First(o => o.PropertyOffset == 0);
            long counted = CountedSegments(m);
            m.BeginEdit("add and remove");
            m.AddProperty(bare, 0x11, 7, 0);
            m.RemoveProperty(bare, m.PropertiesOf(bare).Single());
            m.EndEdit();
            Assert.Equal(0, bare.PropertyOffset);
            Assert.Empty(m.PropertiesOf(bare));
            Assert.Equal(counted, CountedSegments(m));
        }

        [Fact]
        public void TypeFieldsWriteInPlace()
        {
            var bytes = File.ReadAllBytes(GameData.PathOf(Path.Combine("static", "TYPES.DAT")));
            var t = TypeTable.Read(bytes);
            Assert.Equal(bytes, t.ToBytes());
            int sword = Enumerable.Range(0, t.Count).First(i => t[i].HitPoints > 0);
            int hp = t[sword].HitPoints;
            t.Set(sword, TypeField.HitPoints, 200);
            t.Set(sword, TypeField.Weight, 12);
            Assert.Equal(200, t[sword].HitPoints);
            Assert.Equal(12, t.Get(sword, TypeField.Weight));
            var changed = t.ToBytes();
            var differ = Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != changed[i]).ToList();
            Assert.All(differ, i => Assert.Contains(i, new[] { 8 + 16 * sword + 0xA, 8 + 16 * sword + 0xD }));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Set(sword, TypeField.HitPoints, 256));
            t.Set(sword, TypeField.HitPoints, hp);
        }
    }
}
