using System;
using System.Linq;
using System.Reflection;
using U9.Formats.Terrain;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class TransitionTests
    {
        static MapEditor Britannia(out IDisposable keep)
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            keep = (IDisposable)b;
            return (MapEditor)b.GetType().GetField("Editor").GetValue(b);
        }

        [Fact]
        public void BritanniasSetsBlendTheGroundTheyLieBetween()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var tr = ed.Transitions;
                Assert.Equal((8, 11), (tr.Sets[10].A, tr.Sets[10].B));     // grass to dirt
                Assert.Equal((24, 11), (tr.Sets[36].A, tr.Sets[36].B));
                Assert.Equal((8, 4), (tr.Sets[1].A, tr.Sets[1].B));
                Assert.False(tr.Sets.ContainsKey(23));                       // its sides are not clear
                Assert.Equal(new[] { 0, 1, 2, 3 }, tr.PlainFrames[8]);
            }
        }

        [Fact]
        public void TheMapsOwnTransitionQuadsComeBackFromTheirCorners()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var tr = ed.Transitions;
                var r = ed.Raster;
                var rnd = new Random(1);
                var corners = new int[4];
                int n = 0, same = 0;
                for (int y = 0; y < r.Height - 1; y++)
                    for (int x = 0; x < r.Width - 1; x++)
                    {
                        var p = r[x, y];
                        if (!tr.Sets.TryGetValue(p.Texture, out var s) || p.Hole) continue;
                        for (int k = 0; k < 4; k++) corners[k] = tr.CornerMaterial(p, k & 1, k >> 1);
                        n++;
                        if (tr.TryQuad(corners, rnd, out int tex, out int frame, out int rot) && tex == p.Texture && frame / 3 == p.Frame / 3 && rot == p.Rotation) same++;
                    }
                Assert.True(n > 30000, $"{n} quads");
                Assert.True(same > n * 0.99, $"{same} of {n}");
            }
        }

        [Fact]
        public void DirtPaintedOnGrassGetsGrassToDirtEdgesThatMeet()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var r = ed.Raster;
                // a stretch of plain grass, 24 points square, laid first
                (int X, int Y) at = (300, 300);
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Paint, Texture = 8, Frame = 1, Radius = 18 });
                ed.Dab(312, 312);
                ed.EndStroke();
                float cx = at.X + 12, cy = at.Y + 12;

                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Paint, Blend = true, Texture = 11, Radius = 4 });
                ed.Dab(cx, cy);
                ed.Dab(cx + 2, cy + 1);
                ed.EndStroke();

                var tr = ed.Transitions;
                Assert.Equal(11, r[(int)cx, (int)cy].Texture);
                int edges = 0;
                for (int y = at.Y + 2; y < at.Y + 22; y++)
                    for (int x = at.X + 2; x < at.X + 22; x++)
                    {
                        var p = r[x, y];
                        Assert.Contains(p.Texture, new[] { 8, 10, 11 });
                        if (p.Texture == 10) edges++;
                        // the corner shared with the quad to the east and the one to the north agree
                        Assert.Equal(tr.CornerMaterial(p, 1, 0), tr.CornerMaterial(r[x + 1, y], 0, 0));
                        Assert.Equal(tr.CornerMaterial(p, 1, 1), tr.CornerMaterial(r[x + 1, y], 0, 1));
                        Assert.Equal(tr.CornerMaterial(p, 0, 1), tr.CornerMaterial(r[x, y + 1], 0, 0));
                    }
                Assert.True(edges >= 16, $"{edges} edge quads");
                ed.Undo();
                Assert.Equal(8, r[(int)cx, (int)cy].Texture);
                ed.Undo();
                Assert.False(ed.HasChanges);
            }
        }
    }
}
