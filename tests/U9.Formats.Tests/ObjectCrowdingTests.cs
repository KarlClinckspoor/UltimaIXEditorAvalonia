using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Objects;
using Xunit;

namespace U9.Formats.Tests
{
    /// <summary>
    /// Many copies, deletions and moves in the busiest chunks of every shipped object file must leave every other
    /// object as it was. (A page's +0x04 is not a dependable end of its records: trusting it, copies overwrote
    /// objects in about a third of the fixed pages.)
    /// </summary>
    public class ObjectCrowdingTests
    {
        public static IEnumerable<object[]> AllFiles() =>
            GameData.Files("static", n => n.StartsWith("fixed.", StringComparison.OrdinalIgnoreCase) && int.TryParse(Path.GetExtension(n).TrimStart('.'), out _)).Select(f => new object[] { f, true })
            .Concat(GameData.Files("runtime", n => n.StartsWith("nonfixed.", StringComparison.OrdinalIgnoreCase) && int.TryParse(Path.GetExtension(n).TrimStart('.'), out _)).Select(f => new object[] { f, false }));

        // what an object is, without where its record sits or what its list link is
        static string What(MapObject o) => $"{o.MapX} {o.MapY} {o.Z} {o.Type} {o.Rotation} {o.Flags} {Convert.ToHexString(o.Raw.AsSpan(4))}";

        [Theory]
        [MemberData(nameof(AllFiles))]
        public void CrowdingTheBusiestChunksChangesNoOtherObject(string relative, bool isFixed)
        {
            GameData.Require();
            var kind = isFixed ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;
            var m = ObjectMap.Read(File.ReadAllBytes(GameData.PathOf(relative)), kind);
            if (m.Objects.Count == 0) return;
            var before = m.Objects.ToDictionary(o => o, What);
            int count = m.Objects.Count;
            var touched = new HashSet<MapObject>();
            var busiest = m.Objects.GroupBy(o => (o.ChunkX, o.ChunkY)).OrderByDescending(g => g.Count()).Take(4).Select(g => g.ToList()).ToList();
            foreach (var chunk in busiest)
            {
                var movable = chunk.Where(m.CanLeaveChunk).Take(12).ToList();
                foreach (var o in movable)
                {
                    touched.Add(o);
                    var copy = m.Duplicate(o, o.MapX + (o.X % ObjectMap.SubchunkUnits == 0 ? 1 : -1), o.MapY, o.Z);     // same subchunk, one unit aside
                    count++;
                    Assert.Equal(o.Type, copy.Type);
                }
                foreach (var o in movable.Take(3)) { m.Remove(o); count--; }
                foreach (var o in movable.Skip(3).Take(3)) o.MoveTo(o.MapX, o.MapY, Math.Min(0xFFFF, o.Z + 1));
            }
            // objects from elsewhere moved into the busiest chunk
            var target = busiest[0][0];
            foreach (var o in m.Objects.Where(o => (o.ChunkX, o.ChunkY) != (target.ChunkX, target.ChunkY) && m.CanLeaveChunk(o) && !touched.Contains(o)).Take(10).ToList())
            {
                touched.Add(o);
                o.MoveTo(target.ChunkX + 2048, target.ChunkY + 2048, o.Z);
            }

            foreach (var (o, was) in before)
                if (!touched.Contains(o)) Assert.True(was == What(o), $"{relative}: an object the edits did not touch changed: {was} -> {What(o)}");
            var back = ObjectMap.Read(m.ToBytes(), kind);
            Assert.Equal(count, back.Objects.Count);
            var saved = back.Objects.Select(What).ToHashSet();
            foreach (var o in m.Objects) Assert.Contains(What(o), saved);
        }
    }
}
