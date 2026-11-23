using System;
using System.IO;
using System.Linq;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using Xunit;

namespace U9.Formats.Tests
{
    public class ObjectTests
    {
        public static TheoryData<string> AllObjectFiles()
        {
            var d = new TheoryData<string>();
            foreach (string f in GameData.Files("static", n => n.StartsWith("fixed.", StringComparison.OrdinalIgnoreCase))) d.Add(f);
            foreach (string f in GameData.Files("runtime", n => n.StartsWith("nonfixed.", StringComparison.OrdinalIgnoreCase))) d.Add(f);
            return d;
        }

        static ObjectFileKind KindOf(string relative) =>
            Path.GetFileName(relative).StartsWith("fixed", StringComparison.OrdinalIgnoreCase) ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;

        [Theory]
        [MemberData(nameof(AllObjectFiles))]
        public void LoadsAndWritesBackUnchanged(string relative)
        {
            string path = GameData.PathOf(relative);
            byte[] original = File.ReadAllBytes(path);
            var m = ObjectMap.Read(original, KindOf(relative));
            Assert.Equal(original, m.ToBytes());
            foreach (var o in m.Objects)
            {
                Assert.InRange(o.X, 0, 4095);
                Assert.InRange(o.Y, 0, 4095);
            }
        }

        [Fact]
        public void BritanniaFixedObjectsStandOnTheTerrain()
        {
            var t = TerrainMap.Load(GameData.PathOf(Path.Combine("static", "terrain.9")));
            var m = ObjectMap.Load(GameData.PathOf(Path.Combine("static", "fixed.9")), ObjectFileKind.Fixed);
            Assert.True(m.Objects.Count > 15000, $"{m.Objects.Count} objects");
            var errors = m.Objects
                .Where(o => o.PointX < 1024 && o.PointRow < 1024)
                .Select(o => (o, h: t.GetPoint((int)o.PointX, (int)o.PointRow).Height))
                .Where(p => p.h > 50)
                .Select(p => Math.Abs(p.o.TerrainHeight - p.h))
                .OrderBy(e => e).ToList();
            Assert.True(errors[errors.Count / 2] < 20, $"median height error {errors[errors.Count / 2]}");
        }

        [Fact]
        public void LordBritishsThroneIsInCastleBritish()
        {
            var m = ObjectMap.Load(GameData.PathOf(Path.Combine("runtime", "nonfixed.9")), ObjectFileKind.Nonfixed);
            var throne = Assert.Single(m.Objects, o => o.Type == 2894);
            Assert.InRange(throne.PointX, 500, 505);
            Assert.InRange(throne.PointRow, 552, 557);
        }

        [Fact]
        public void MovingAnObjectChangesOnlyItsPosition()
        {
            string path = GameData.PathOf(Path.Combine("static", "fixed.9"));
            byte[] original = File.ReadAllBytes(path);
            var m = ObjectMap.Read((byte[])original.Clone(), ObjectFileKind.Fixed);
            var o = m.Objects[100];
            int type = o.Type, offset = o.FileOffset;
            o.SetPosition(1234, 2345, 3456);
            var back = ObjectMap.Read(m.ToBytes(), ObjectFileKind.Fixed);
            var b = back.Objects.Single(x => x.FileOffset == offset);
            Assert.Equal((1234, 2345, 3456, type), (b.X, b.Y, b.Z, b.Type));
            var diff = Enumerable.Range(0, original.Length).Count(i => original[i] != m.Data[i]);
            Assert.InRange(diff, 1, 6);
        }

        [Fact]
        public void TypeNamesLoad()
        {
            var n = TypeNames.Load(GameData.PathOf(Path.Combine("static", "TYPENAME.FLX")));
            Assert.Equal("Lord British", n[1]);
            Assert.Equal("Lord British's throne", n[2894]);
        }
    }
}
