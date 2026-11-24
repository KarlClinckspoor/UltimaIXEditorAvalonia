using System.IO;
using System.Linq;
using U9.Formats.Bitmaps;
using U9.Formats.Flx;
using Xunit;

namespace U9.Formats.Tests
{
    public class BitmapTests
    {
        static (double R, double G, double B, double A) Average(BitmapFrame f)
        {
            double r = 0, g = 0, b = 0, a = 0; int n = f.Width * f.Height;
            for (int i = 0; i < n; i++) { r += f.Rgba[i * 4]; g += f.Rgba[i * 4 + 1]; b += f.Rgba[i * 4 + 2]; a += f.Rgba[i * 4 + 3]; }
            return (r / n, g / n, b / n, a / n);
        }

        [Fact]
        public void SeaFloorTextureIsSandy()
        {
            using var a = FlxArchive.Open(GameData.PathOf(Path.Combine("static", "bitmap16.flx")));
            var rec = a.Read(24);
            Assert.Equal(4, Bitmap16.FrameCount(rec));
            var f = Bitmap16.ReadFrame(rec, 0);
            Assert.Equal(128, f.Width);
            Assert.Equal(128, f.Height);
            var c = Average(f);
            Assert.True(c.R > c.G && c.G > c.B, $"sand should be red > green > blue, got {c}");
            Assert.Equal(255, c.A);
        }

        [Fact]
        public void GrassTextureIsGreen()
        {
            using var a = FlxArchive.Open(GameData.PathOf(Path.Combine("static", "bitmap16.flx")));
            var c = Average(Bitmap16.ReadFrame(a.Read(8), 0));
            Assert.True(c.G > c.R && c.G > c.B, $"grass should be green, got {c}");
        }

        [Fact]
        public void EveryFrameOfEveryRecordDecodes()
        {
            using var a = FlxArchive.Open(GameData.PathOf(Path.Combine("static", "bitmap16.flx")));
            int frames = 0;
            foreach (int i in a.UsedIndices())
            {
                var rec = a.Read(i);
                for (int k = 0; k < Bitmap16.FrameCount(rec); k++) { Bitmap16.ReadFrame(rec, k); frames++; }
            }
            Assert.True(frames > 6599);
        }

        [Fact]
        public void MagentaIsTransparent()
        {
            using var a = U9.Formats.Flx.FlxArchive.Open(GameData.PathOf(System.IO.Path.Combine("static", "bitmap16.flx")));
            foreach (int record in new[] { 1765, 2501 })      // ARGB1555 and RGB565 frames that are all, or nearly all, key colour
            {
                var f = Bitmap16.ReadFrame(a.Read(record), 0);
                int clear = 0;
                for (int i = 3; i < f.Rgba.Length; i += 4) if (f.Rgba[i] == 0) clear++;
                Assert.True(clear > f.Width * f.Height * 0.9, $"record {record}: {clear} of {f.Width * f.Height} transparent");
                for (int i = 0; i < f.Rgba.Length; i += 4)
                    Assert.False(f.Rgba[i + 3] != 0 && f.Rgba[i] > 240 && f.Rgba[i + 1] < 8 && f.Rgba[i + 2] > 240, $"record {record}: opaque magenta left");
            }
        }
    }
}
