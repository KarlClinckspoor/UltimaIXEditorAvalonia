using System;
using System.IO;
using System.Linq;
using System.Reflection;
using U9.Formats.Terrain;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class TerrainEditTests
    {
        static MapEditor Britannia(out IDisposable keep)
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            keep = (IDisposable)b;
            return (MapEditor)b.GetType().GetField("Editor").GetValue(b);
        }

        [Fact]
        public void RaisingLiftsTheGroundAndTheThroneAndUndoPutsBothBack()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
                int tx = (int)MathF.Round(throne.Source.MapX / 128f), ty = (int)MathF.Round(throne.Source.MapY / 128f);
                int z = throne.Source.Z;
                int ground = ed.GroundZ(throne.Source.MapX, throne.Source.MapY);
                // the throne stands on a dais (a model), not on the terrain: far off the ground, it must stay put
                bool onGround = Math.Abs(z - ground) <= 16;
                int h = ed.Raster.HeightAt(tx, ty);
                int edited = 0;
                ed.TerrainEdited += (x, y, w, hh) => edited++;

                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Raise, Radius = 3, Strength = 20 });
                ed.Dab(tx, ty);
                ed.Dab(tx, ty);
                ed.EndStroke();

                Assert.True(ed.TerrainChanged);
                Assert.Equal("raise", ed.UndoDescription);
                Assert.True(edited >= 2);
                Assert.Equal(h + 40, ed.Raster.HeightAt(tx, ty));
                Assert.Equal(ed.Raster.Points[tx + ty * ed.Raster.Width], ed.Terrain.GetPoint(tx, ty).Raw);
                if (onGround) Assert.True(throne.Source.Z > z); else Assert.Equal(z, throne.Source.Z);
                // points four away are outside the brush
                Assert.Equal(ed.Raster.HeightAt(tx + 5, ty), ed.Terrain.GetPoint(tx + 5, ty).Height);

                ed.Undo();
                Assert.False(ed.HasChanges);
                Assert.Equal(z, throne.Source.Z);
                Assert.Equal(h, ed.Raster.HeightAt(tx, ty));
                ed.Redo();
                Assert.True(ed.TerrainChanged);
            }
        }

        [Fact]
        public void TreesOnTheGroundFollowItAndFloatingThingsDoNot()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                // a fixed object standing on the terrain
                var tree = ed.Fixed.Objects.First(o => !o.Removed && Math.Abs(o.Z - ed.GroundZ(o.MapX, o.MapY)) <= 2 && o.MapX > 200 * 128 && o.MapY > 200 * 128);
                int z = tree.Z;
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Lower, Radius = 4, Strength = 40 });
                ed.Dab(tree.MapX / 128f, tree.MapY / 128f);
                // during the stroke the tree is only drawn lower; its record changes when the stroke ends
                var preview = ed.FollowPreview().Single(p => p.Object.Source == tree);
                Assert.True(preview.Z < z - 40);
                Assert.Equal(z, tree.Z);
                ed.EndStroke();
                Assert.Equal(preview.Z, tree.Z);
                Assert.True(tree.Z < z - 40);
                Assert.InRange(tree.Z - ed.GroundZ(tree.MapX, tree.MapY), -2, 2);
                Assert.True(ed.FixedChanged);
                ed.Undo();
                Assert.Equal(z, tree.Z);
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void FlattenSmoothPaintAndHoles()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Flatten, Radius = 3, Strength = 1, FlattenTo = 700, ObjectsFollow = false });
                ed.Dab(300, 300);
                ed.EndStroke();
                Assert.Equal(700, ed.Raster.HeightAt(300, 300));

                var before = ed.Raster[400, 400];
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Paint, Radius = 1.5f, Texture = 5, Frame = 2 });
                ed.Dab(400, 400);
                ed.EndStroke();
                Assert.Equal((5, 2, before.Height), (ed.Raster[400, 400].Texture, ed.Raster[400, 400].Frame, ed.Raster[400, 400].Height));

                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Hole, Radius = 0.5f });
                ed.Dab(410, 410);
                ed.EndStroke();
                Assert.True(ed.Raster[410, 410].Hole);

                // smoothing a spike brings it towards its neighbours
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Raise, Radius = 0.5f, Strength = 200, ObjectsFollow = false });
                ed.Dab(500, 300);
                ed.EndStroke();
                int spike = ed.Raster.HeightAt(500, 300);
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Smooth, Radius = 2, Strength = 1, ObjectsFollow = false });
                ed.Dab(500, 300);
                ed.EndStroke();
                Assert.True(ed.Raster.HeightAt(500, 300) < spike - 100);

                for (int i = 0; i < 5; i++) ed.Undo();
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void UndoDuringAStrokeTakesBackTheStroke()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                int h = ed.Raster.HeightAt(320, 320);
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Raise, Radius = 2, Strength = 30, ObjectsFollow = false });
                ed.Dab(320, 320);
                Assert.Equal("raise", ed.Undo());
                Assert.False(ed.InStroke);
                Assert.Equal(h, ed.Raster.HeightAt(320, 320));
                ed.Dab(320, 320);                   // the view may still send dabs: nothing happens
                Assert.Equal(h, ed.Raster.HeightAt(320, 320));
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void RoughenRaisesSomePointsAndLowersOthers()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var pts = Enumerable.Range(0, 17 * 17).Select(k => (X: 342 + k % 17, Y: 342 + k / 17)).ToList();
                var before = pts.Select(p => ed.Raster.HeightAt(p.X, p.Y)).ToList();
                ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Roughen, Radius = 10, Strength = 20, ObjectsFollow = false });
                for (int i = 0; i < 5; i++) ed.Dab(350, 350);
                ed.EndStroke();
                var d = pts.Select((p, i) => ed.Raster.HeightAt(p.X, p.Y) - before[i]).ToList();
                Assert.True(d.Count(x => x > 5) > 10, $"{d.Count(x => x > 5)} raised");
                Assert.True(d.Count(x => x < -5) > 10, $"{d.Count(x => x < -5)} lowered");
                Assert.Equal("roughen", ed.UndoDescription);
                ed.Undo();
                Assert.Equal(before, pts.Select(p => ed.Raster.HeightAt(p.X, p.Y)).ToList());
            }
        }

        [Fact]
        public void TheWaterLevelIsOneUndoStep()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                Assert.Equal(2024, ed.Terrain.WaterLevel);          // height 506
                int seen = -1;
                ed.WaterEdited += w => seen = w;
                ed.SetWaterHeight(520.25f);
                Assert.Equal((2081, 2081), (ed.Terrain.WaterLevel, seen));
                Assert.Equal("water level", ed.UndoDescription);
                Assert.True(ed.TerrainChanged);
                ed.Undo();
                Assert.Equal((2024, 2024), (ed.Terrain.WaterLevel, seen));
                Assert.False(ed.HasChanges);
                Assert.Equal(2024, TerrainMap.Read(ed.Terrain.ToBytes()).WaterLevel);
            }
        }

        [Fact]
        public void UndoThenANewStrokeAfterSavingIsStillUnsaved()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                string dir = Path.Combine(Path.GetTempPath(), "u9terrain-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    ed.TerrainPath = Path.Combine(dir, "terrain.9");
                    File.WriteAllBytes(ed.TerrainPath, ed.Terrain.ToBytes());
                    var b = new TerrainBrush { Tool = TerrainTool.Raise, Radius = 2, Strength = 30, ObjectsFollow = false };
                    ed.BeginStroke(b); ed.Dab(600, 600); ed.EndStroke();
                    ed.Save(null, null);
                    Assert.False(ed.HasChanges);
                    ed.Undo();
                    Assert.True(ed.HasChanges);                 // the file holds the stroke, the editor does not
                    ed.BeginStroke(b); ed.Dab(610, 610); ed.EndStroke();
                    Assert.True(ed.HasChanges);                 // was false with step counters
                    // saving during a stroke finishes it first, so it is saved whole
                    ed.BeginStroke(b); ed.Dab(620, 620);
                    ed.Save(null, null);
                    Assert.False(ed.InStroke);
                    Assert.False(ed.HasChanges);
                    Assert.Equal(ed.Raster.HeightAt(620, 620), TerrainMap.Load(ed.TerrainPath).GetPoint(620, 620).Height);
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public void SavingWritesTheTerrainAndKeepsTheOriginal()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                string dir = Path.Combine(Path.GetTempPath(), "u9terrain-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string path = Path.Combine(dir, "terrain.9");
                    File.WriteAllBytes(path, ed.Terrain.ToBytes());
                    ed.TerrainPath = path;
                    ed.BeginStroke(new TerrainBrush { Tool = TerrainTool.Raise, Radius = 2, Strength = 30, ObjectsFollow = false });
                    ed.Dab(600.5f, 600.5f);
                    ed.EndStroke();
                    var written = ed.Save(null, null);
                    Assert.Equal(new[] { path }, written);
                    Assert.True(File.Exists(path + ".orig"));
                    Assert.False(ed.HasChanges);
                    var back = TerrainMap.Load(path);
                    Assert.Equal(ed.Raster.HeightAt(600, 600), back.GetPoint(600, 600).Height);
                    Assert.NotEqual(TerrainMap.Load(path + ".orig").GetPoint(600, 600).Height, back.GetPoint(600, 600).Height);
                }
                finally { Directory.Delete(dir, true); }
            }
        }
    }
}
