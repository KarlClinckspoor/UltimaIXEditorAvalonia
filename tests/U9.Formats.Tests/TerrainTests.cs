using System;
using System.IO;
using System.Linq;
using U9.Formats.Terrain;
using Xunit;

namespace U9.Formats.Tests
{
    public class TerrainTests
    {
        public static TheoryData<string> AllTerrain()
        {
            var d = new TheoryData<string>();
            foreach (string f in GameData.Files("static", n => n.StartsWith("terrain.", StringComparison.OrdinalIgnoreCase))) d.Add(f);
            return d;
        }

        [Theory]
        [MemberData(nameof(AllTerrain))]
        public void RoundTripIsByteIdentical(string relative)
        {
            string path = GameData.PathOf(relative);
            byte[] original = File.ReadAllBytes(path);
            var t = TerrainMap.Read(original);
            Assert.Equal(original, t.ToBytes());
        }

        [Fact]
        public void AllShippedMapsLoad()
        {
            GameData.Require();
            int n = 0;
            foreach (string f in GameData.Files("static", s => s.StartsWith("terrain.", StringComparison.OrdinalIgnoreCase)))
            {
                TerrainMap.Load(GameData.PathOf(f));
                n++;
            }
            Assert.Equal(168, n);
        }

        [Fact]
        public void BritanniaHeader()
        {
            var t = TerrainMap.Load(GameData.PathOf(Path.Combine("static", "terrain.9")));
            Assert.Equal("Britannia", t.Name);
            Assert.Equal(1024, t.Width);
            Assert.Equal(1024, t.Height);
            Assert.Equal(64, t.TilesX);
            Assert.Equal(2024, t.WaterLevel);
            Assert.Equal(10.0f, t.WaveAmplitude);
            Assert.Equal(0x4Eu, t.Flags);
            Assert.Equal(5588, t.Chunks.Count);
            Assert.Null(t.DeclaredChunkCount);
            Assert.Empty(t.TrailingData);
        }

        [Fact]
        public void AbyssWaterLevelIsSigned()
        {
            var t = TerrainMap.Load(GameData.PathOf(Path.Combine("static", "terrain.11")));
            Assert.Equal(-12, t.WaterLevel);
        }

        [Fact]
        public void EditingASharedChunkCopiesIt()
        {
            // Britannia's tiles all have their own chunk, so build a map where they share one.
            var t = new TerrainMap(32, 32, "test");
            Assert.Single(t.Chunks);
            t.SetPoint(17, 3, t.GetPoint(17, 3).WithHeight(100));
            Assert.Equal(2, t.Chunks.Count);
            Assert.Equal(100, t.GetPoint(17, 3).Height);
            Assert.Equal(0, t.GetPoint(1, 3).Height);          // the other tiles still see the old chunk
            t.SetPoint(18, 3, t.GetPoint(18, 3).WithHeight(50));
            Assert.Equal(2, t.Chunks.Count);                    // already its own chunk now

            var back = TerrainMap.Read(t.ToBytes());
            Assert.Equal("test", back.Name);
            Assert.Equal(100, back.GetPoint(17, 3).Height);
            Assert.Equal(50, back.GetPoint(18, 3).Height);
        }

        [Fact]
        public void EditedMapWritesAndReadsBack()
        {
            var t = TerrainMap.Load(GameData.PathOf(Path.Combine("static", "terrain.9")));
            var p = t.GetPoint(500, 600);
            var q = p.WithHeight((p.Height + 7) & TerrainPoint.MaxHeight).WithTexture(123).WithFrame(5).WithHole(!p.Hole);
            t.SetPoint(500, 600, q);
            var back = TerrainMap.Read(t.ToBytes());
            Assert.Equal(q, back.GetPoint(500, 600));
            Assert.Equal(t.GetPoint(501, 600), back.GetPoint(501, 600));
        }

        [Fact]
        public void PointBitsRoundTrip()
        {
            var p = new TerrainPoint(0).WithHeight(4095).WithTexture(1023).WithFrame(63).WithHole(true).WithSwap(true).WithMirror(true);
            Assert.Equal(0xFFFF7FFFu, p.Raw);
            Assert.Equal(4095, p.Height);
            Assert.Equal(1023, p.Texture);
            Assert.Equal(63, p.Frame);
            Assert.True(p.Hole && p.Swap && p.Mirror && !p.Bit15);
            Assert.Equal(0u, p.WithHeight(0).WithTexture(0).WithFrame(0).WithHole(false).WithSwap(false).WithMirror(false).Raw);
        }
    }
}
