using System;
using System.IO;
using U9.Formats.Flx;
using U9.Formats.Terrain;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class TextureTests
    {
        [Fact]
        public void BritanniaTexturesAllLoad()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var t = TerrainMap.Load(Path.Combine(g.StaticFolder, "terrain.9"));
            var r = new TerrainRaster(t);
            using var bmp = FlxArchive.Open(Path.Combine(g.StaticFolder, "bitmap16.flx"));
            var set = TerrainTextures.Build(r, i => bmp.Read(i));
            Assert.Empty(set.Missing);
            Assert.Equal(285, set.Layers.Count);
            var p = r[500, 470];
            Assert.NotEqual(0, set.Lookup[p.Texture * TerrainTextures.Frames + p.Frame]);
        }
    }
}
