using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Objects;
using Xunit;

namespace U9.Formats.Tests
{
    public class ObjectTreeTests
    {
        static ObjectMap Nonfixed(int map) => ObjectMap.Load(GameData.PathOf(Path.Combine("runtime", $"nonfixed.{map}")), ObjectFileKind.Nonfixed);

        /// <summary>A tree's bytes with the file offsets and the position zeroed: what must survive a copy.</summary>
        static string Shape(ObjectTree t)
        {
            var r = (byte[])t.Record.Clone();
            Array.Clear(r, 0, 10);              // list link, x, y, z
            if (r.Length == 32) Array.Clear(r, 0x1C, 4);
            var parts = new List<string> { Convert.ToHexString(r) };
            foreach (var s0 in t.Segments)
            {
                var s = (byte[])s0.Clone();
                for (int k = 0; k < Math.Min(3, (int)s[0]); k++)
                    if (s[1 + k] == PropertyNames.NextPropertyPointer || s[1 + k] == PropertyNames.Contents) Array.Clear(s, 4 + 4 * k, 4);
                parts.Add(Convert.ToHexString(s));
            }
            foreach (var c in t.Contents) parts.Add($"[{c.Segment}.{c.Place}: {string.Join(" | ", c.Items.Select(Shape))}]");
            return string.Join(" ", parts);
        }

        /// <summary>Per file: the property segments the pages count, against those reachable from the listed objects.</summary>
        static (long Counted, long Reached) Segments(ObjectMap m)
        {
            long counted = 0;
            for (int cy = 0; cy < m.Height; cy++)
                for (int cx = 0; cx < m.Width; cx++)
                    foreach (int p in m.PagesOf(cx, cy)) counted += BinaryPrimitives.ReadUInt32LittleEndian(m.Data.AsSpan(m.DataStart + p + 0x18));
            long reached = 0;
            void Count(ObjectTree t) { reached += t.Segments.Count; foreach (var i in t.Inside) reached += i.Segments.Count; }
            foreach (var o in m.Objects) Count(m.TreeOf(o));
            return (counted, reached);
        }

        [Fact]
        public void ATreeWithContentsAndAChainCopiesWholeIntoAnotherChunkAndFile()
        {
            var m = Nonfixed(9);
            var bag = m.Objects.First(o => m.TreeOf(o).Inside.Any(i => i.Segments.Count > 0));
            var chained = m.Objects.First(o => m.TreeOf(o).Segments.Count > 1);
            var trees = new[] { m.TreeOf(bag), m.TreeOf(chained) };
            Assert.Contains(trees[1].Segments[0].Skip(1).Take(3), id => id == PropertyNames.NextPropertyPointer);
            var before = m.ToBytes();
            var (counted0, reached0) = Segments(m);
            Assert.Equal(counted0, reached0);

            // into a chunk far from both, then into another map's file
            int x = (bag.ChunkX + 9 * 4096) % (m.Width * 4096) + 1000, y = (bag.ChunkY + 5 * 4096) % (m.Height * 4096) + 2000;
            m.BeginEdit("copy");
            var copies = trees.Select((t, k) => m.AddTree(t, x + 300 * k, y, 2500)).ToList();
            var edit = m.EndEdit();
            for (int k = 0; k < 2; k++) Assert.Equal(Shape(trees[k]), Shape(m.TreeOf(copies[k])));

            var reread = ObjectMap.Read(m.ToBytes(), ObjectFileKind.Nonfixed);
            for (int k = 0; k < 2; k++)
            {
                var c = reread.Objects.Single(o => o.MapX == x + 300 * k && o.MapY == y && o.Z == 2500);
                Assert.Equal(Shape(trees[k]), Shape(reread.TreeOf(c)));
                Assert.Equal(m.PropertiesOf(k == 0 ? bag : chained).Select(p => (p.Id, p.A, p.B)).Where(p => p.Id != PropertyNames.Contents),
                             reread.PropertiesOf(c).Select(p => (p.Id, p.A, p.B)).Where(p => p.Id != PropertyNames.Contents));
            }
            var (counted1, reached1) = Segments(reread);
            Assert.Equal(counted1, reached1);
            Assert.Equal(counted0 + trees.Sum(t => t.Segments.Count + t.Inside.Sum(i => i.Segments.Count)), counted1);

            m.Undo(edit);
            Assert.Equal(before, m.Data);

            var other = Nonfixed(4);
            other.BeginEdit("copy");
            var there = other.AddTree(trees[0], 600, 700, 100);
            other.EndEdit();
            Assert.Equal(Shape(trees[0]), Shape(ObjectMap.Read(other.ToBytes(), ObjectFileKind.Nonfixed).TreeOf(
                ObjectMap.Read(other.ToBytes(), ObjectFileKind.Nonfixed).Objects.Single(o => o.MapX == 600 && o.MapY == 700))));
            Assert.Equal(trees[0].Inside.Count(), other.ContentsOf(there).Count + other.ContentsOf(there).Sum(i => other.TreeOf(i).Inside.Count()));
        }
    }
}
