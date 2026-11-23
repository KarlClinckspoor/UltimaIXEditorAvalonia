using System;
using System.Numerics;
using U9.Formats.Terrain;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class PickingTests
    {
        /// <summary>A 64 x 64 map, flat at height 320 (10 world units), with a 32-unit (1 world unit) step from row 40 on.</summary>
        static TerrainMap StepMap()
        {
            var t = new TerrainMap(64, 64, "step");
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    t.SetPoint(x, y, t.GetPoint(x, y).WithHeight(y >= 40 ? 352 : 320));
            return t;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void StraightDownHitsThePointBelow(bool northIsRowZero)
        {
            var t = StepMap();
            var r = new TerrainRaster(t);
            var s = new MapSpace(64, 64) { NorthIsRowZero = northIsRowZero };
            var o = new Vector3(10.5f, 100, s.WorldZ(20.5f));
            var hit = TerrainPicker.Pick(r, s, o, -Vector3.UnitY);
            Assert.True(hit.Hit);
            Assert.Equal(10, hit.X);
            Assert.Equal(20, hit.Row);
            Assert.Equal(10f, hit.World.Y, 2);
        }

        [Fact]
        public void SlantedRayStopsAtTheRaisedPart()
        {
            var t = StepMap();
            var r = new TerrainRaster(t);
            var s = new MapSpace(64, 64) { NorthIsRowZero = true };
            // from above row 30, looking towards higher rows (south) and down: the raised rows 40+ are hit first
            var from = new Vector3(20.5f, 10.8f, s.WorldZ(30));
            var dir = Vector3.Normalize(new Vector3(0, -0.05f, -1));
            var hit = TerrainPicker.Pick(r, s, from, dir);
            Assert.True(hit.Hit);
            Assert.InRange(hit.Row, 38, 40);
            Assert.Equal(20, hit.X);
        }

        [Fact]
        public void RayMissingTheMapHitsNothing()
        {
            var t = StepMap();
            var r = new TerrainRaster(t);
            var s = new MapSpace(64, 64);
            var hit = TerrainPicker.Pick(r, s, new Vector3(-50, 100, -50), Vector3.Normalize(new Vector3(-1, -0.1f, 0)));
            Assert.False(hit.Hit);
        }

        [Fact]
        public void RasterFollowsTheTileTable()
        {
            var t = new TerrainMap(32, 32, "tiles");
            t.SetPoint(20, 5, t.GetPoint(20, 5).WithHeight(77).WithTexture(9));
            var r = new TerrainRaster(t);
            Assert.Equal(77, r[20, 5].Height);
            Assert.Equal(9, r[20, 5].Texture);
            Assert.Equal(0, r[4, 5].Height);
            Assert.Equal(77, r.MaxHeight);
            Assert.Equal(77f, r.HeightAt(20f, 5f));
            Assert.Equal(38.5f, r.HeightAt(19.5f, 5f));
        }

        [Fact]
        public void MapSpaceRowsRoundTrip()
        {
            var s = new MapSpace(1024, 1024) { NorthIsRowZero = true };
            Assert.Equal(1024f, s.WorldZ(0));
            Assert.Equal(123.25f, s.Row(s.WorldZ(123.25f)));
            s.NorthIsRowZero = false;
            Assert.Equal(0f, s.WorldZ(0));
            Assert.Equal(1f, MapSpace.WorldY(32));
            Assert.Equal(1f, MapSpace.WorldYFromObject(128));
        }
    }
}
