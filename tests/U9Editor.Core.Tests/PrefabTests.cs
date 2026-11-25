using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using U9.Formats.Triggers;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class PrefabTests
    {
        static MapEditor Britannia(out IDisposable keep)
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            keep = (IDisposable)b;
            return (MapEditor)b.GetType().GetField("Editor").GetValue(b);
        }

        /// <summary>Any map's editor, without models.</summary>
        static MapEditor Map(int n, TriggerScripts scripts)
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var t = TerrainMap.Load(Path.Combine(g.StaticFolder, $"terrain.{n}"));
            var space = new MapSpace(t.Width, t.Height);
            var fx = ObjectMap.Load(Path.Combine(g.StaticFolder, $"fixed.{n}"), ObjectFileKind.Fixed);
            var nf = ObjectMap.Load(Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), $"nonfixed.{n}", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).First(), ObjectFileKind.Nonfixed);
            return new MapEditor(n, fx, nf, SceneObjects.Build(fx, nf, space, null), space, new TerrainRaster(t)) { Terrain = t, Scripts = scripts };
        }

        // objects round Castle British's throne, both files
        static List<SceneObject> Group(MapEditor ed)
        {
            var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
            return ed.Objects.All.Where(o => !o.Source.Removed &&
                                            Math.Abs(o.Source.MapX - throne.Source.MapX) < 6 * 128 && Math.Abs(o.Source.MapY - throne.Source.MapY) < 6 * 128).ToList();
        }

        [Fact]
        public void APastedGroupKeepsItsShapeAndGroundAndUndoPutsEverythingBack()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = Group(ed);
                Assert.Contains(g, o => o.Kind == ObjectFileKind.Fixed);
                Assert.Contains(g, o => o.Kind == ObjectFileKind.Nonfixed);
                var p = Prefab.Copy(ed, g, margin: 3, ground: true);
                Assert.Equal(g.Count, p.Objects.Count);
                var fixedBefore = ed.Fixed.ToBytes();
                var nonfixedBefore = ed.Nonfixed.ToBytes();
                var pointsBefore = (uint[])ed.Raster.Points.Clone();
                int fixedCount = ed.Fixed.Objects.Count, nonfixedCount = ed.Nonfixed.Objects.Count;

                // 60 points west, no blending: the patch lands exactly, lifted as a whole
                int x = (int)(g.Min(o => o.Source.MapX) / 128) - 3 - 60, row = (int)(g.Min(o => o.Source.MapY) / 128) - 3;
                var r = ed.PastePrefab(p, x, row, new PasteOptions { BlendEdge = false, NewLinks = true, CopyScripts = true });
                Assert.Equal($"paste {g.Count} objects", ed.UndoDescription);
                Assert.Equal(g.Count, r.Objects.Count);
                Assert.Equal(fixedCount + g.Count(o => o.Kind == ObjectFileKind.Fixed), ed.Fixed.Objects.Count);
                Assert.True(ed.TerrainChanged && ed.FixedChanged && ed.NonfixedChanged);
                for (int j = 0; j < p.Height; j++)
                    for (int i = 0; i < p.Width; i++)
                    {
                        var pt = ed.Raster[x + i, row + j];
                        Assert.Equal(Math.Clamp(p.Heights[i + j * p.Width] + r.HeightOffset, 0, 4095), pt.Height);
                        if (i < p.Width - 1 && j < p.Height - 1) Assert.Equal(p.Quads[i + j * (p.Width - 1)], pt.Raw & ~0xFFFu);
                    }
                // each copy stands where its original does, 60 points west (and as high above the ground)
                for (int k = 0; k < g.Count; k++)
                {
                    var o = g[k].Source; var c = r.Objects[k].Source;
                    Assert.Equal((o.MapX - 60 * 128, o.MapY, o.Type), (c.MapX, c.MapY, c.Type));
                    Assert.Equal(o.Z - ed.GroundZ(o.MapX, o.MapY), c.Z - ed.GroundZ(c.MapX, c.MapY));
                    Assert.Equal(o.Rotation, c.Rotation);
                }
                // reread from the bytes, the copies' properties are there
                var reread = ObjectMap.Read(ed.Nonfixed.ToBytes(), ObjectFileKind.Nonfixed);
                Assert.Equal(nonfixedCount + g.Count(o => o.Kind == ObjectFileKind.Nonfixed), reread.Objects.Count);

                ed.Undo();
                Assert.False(ed.HasChanges);
                Assert.Equal(fixedBefore, ed.Fixed.Data);
                Assert.Equal(nonfixedBefore, ed.Nonfixed.Data);
                Assert.Equal(pointsBefore, ed.Raster.Points);
                Assert.All(r.Objects, o => Assert.True(o.Source.Removed));
                ed.Redo();
                Assert.All(r.Objects, o => Assert.False(o.Source.Removed));
                Assert.True(ed.TerrainChanged);
            }
        }

        [Fact]
        public void BlendingFadesTheEdgeIntoTheGroundThere()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var p = Prefab.Copy(ed, Group(ed), margin: 4, ground: true);
                int x = 300, row = 300;
                var before = (uint[])ed.Raster.Points.Clone();
                ed.PastePrefab(p, x, row, new PasteOptions());
                int w = ed.Raster.Width;
                // the outer ring keeps the ground it landed on; the middle is the prefab's, lifted as a whole
                for (int i = 0; i < p.Width; i++) Assert.Equal(before[x + i + row * w] & 0xFFF, ed.Raster.Points[x + i + row * w] & 0xFFF);
                for (int i = 0; i < p.Width; i++) Assert.Equal(before[x + i + (row + p.Height - 1) * w], ed.Raster.Points[x + i + (row + p.Height - 1) * w]);
                int ci = p.Width / 2, cj = p.Height / 2;
                Assert.Equal(p.Quads[ci + cj * (p.Width - 1)], ed.Raster.Points[x + ci + (row + cj) * w] & ~0xFFFu);
                // quads in the outer half of the margin keep their texture
                Assert.Equal(before[x + 1 + (row + cj) * w] & ~0xFFFu, ed.Raster.Points[x + 1 + (row + cj) * w] & ~0xFFFu);
            }
        }

        [Fact]
        public void FourQuarterTurnsGiveThePrefabBackAndOneTurnsGroundAndTextures()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var p = Prefab.Copy(ed, Group(ed), margin: 2, ground: true);
                var back = p.Turned(4);
                Assert.Equal(p.Heights, back.Heights);
                Assert.Equal(p.Quads, back.Quads);
                Assert.Equal(p.Objects.Select(o => (o.X, o.Y)), back.Objects.Select(o => (o.X, o.Y)));
                for (int k = 0; k < p.Objects.Count; k++)
                {
                    var a = Prefab.RotationOf(p.Objects[k].Tree.Record); var b = Prefab.RotationOf(back.Objects[k].Tree.Record);
                    Assert.True(Math.Abs(System.Numerics.Quaternion.Dot(a, b)) > 0.9999f);
                }

                var t = p.Turned(1);
                Assert.Equal((p.Height, p.Width), (t.Width, t.Height));
                int W = p.Width, H = p.Height;
                for (int j = 0; j < H; j++)
                    for (int i = 0; i < W; i++) Assert.Equal(p.Heights[i + j * W], t.Heights[(H - 1 - j) + i * H]);
                // a quad's texture turns with it (one clockwise quarter less), its texture and frame stay
                uint q = p.Quads[1 + 2 * (W - 1)], tq = t.Quads[(H - 2 - 2) + 1 * (H - 1)];
                Assert.Equal(q & ~(3u << 13), tq & ~(3u << 13));
                Assert.Equal(((q >> 13) + 3) & 3, (tq >> 13) & 3);
                // an object east of the middle ends up north of it, facing a quarter further round
                var o = p.Objects[0]; var to = t.Objects[0];
                Assert.Equal(((H - 1) * 128 - o.Y, o.X), (to.X, to.Y));
                float yaw = MapEditor.Yaw(Prefab.RotationOf(o.Tree.Record)), tyaw = MapEditor.Yaw(Prefab.RotationOf(to.Tree.Record));
                Assert.Equal(0, ((tyaw - yaw - 90) % 360 + 540) % 360 - 180, 1);
            }
        }

        [Fact]
        public void PrefabFilesRoundTrip()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = ed.Objects.All.Where(o => !o.Source.Removed && o.Kind == ObjectFileKind.Nonfixed && o.Source.Map.TreeOf(o.Source).Inside.Any()).Take(2)
                          .Concat(Group(ed)).ToList();
                var p = Prefab.Copy(ed, g, 2, ground: true);
                string path = Path.Combine(Path.GetTempPath(), $"u9-test-{Guid.NewGuid():N}{Prefab.Extension}");
                try
                {
                    p.Save(path);
                    var back = Prefab.Load(path);
                    Assert.Equal(p.ToJson().ToJsonString(), back.ToJson().ToJsonString());
                    Assert.Equal(p.Objects.Sum(o => o.Tree.Inside.Count()), back.Objects.Sum(o => o.Tree.Inside.Count()));
                }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public void LinksMoveUpTogetherAndScriptsGetCopiesWithTheirLinks()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                // a zone whose script runs, and an object carrying a link
                var zone = ed.Objects.All.First(o => o.Kind == ObjectFileKind.Nonfixed && o.Source.Link > 0 && o.Source.Link < 0xF000 &&
                                                     Prefab.SlotsOf(o.Source.Map.TreeOf(o.Source)).Any(s => ed.Scripts.IsUsed(s.Slot)));
                var p = Prefab.Copy(ed, new[] { zone }, 1, ground: false);
                int slot = Prefab.SlotsOf(p.Objects[0].Tree).First(s => ed.Scripts.IsUsed(s.Slot)).Slot;
                int freeSlot = ed.Scripts.FreeSlot();
                int expect = ed.LinkShiftFor(new[] { zone.Source.Link });
                Assert.True(expect > 0);            // the original is there
                var r = ed.PastePrefab(p, 200, 200, new PasteOptions());
                var copy = r.Objects.Single().Source;
                Assert.Equal(expect, r.LinkShift);
                Assert.Equal(zone.Source.Link + expect, copy.Link);
                // nothing else here within a script's reach of the new link
                Assert.DoesNotContain(ed.Nonfixed.Objects, o => o != copy && o.Link > 0 && Math.Abs(o.Link - copy.Link) <= 15);
                Assert.True(r.Slots[slot] >= freeSlot);
                var slots = Prefab.SlotsOf(ed.Nonfixed.TreeOf(copy)).Select(s => s.Slot).ToList();
                Assert.Contains(r.Slots[slot], slots);
                Assert.DoesNotContain(slot, slots);
                Assert.Equal(MapEditor.RelinkScript(ed.Scripts[slot], new[] { zone.Source.Link }, r.LinkShift), ed.Scripts[r.Slots[slot]]);
                Assert.True(ed.ScriptsChanged);
                ed.Undo();
                Assert.False(ed.Scripts.IsUsed(r.Slots[slot]));
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void AbsoluteLinksMoveExceptTheArrivalOfAMapChange()
        {
            var ops = new[]
            {
                new TriggerOp(0x1011, 0, 5), new TriggerOp(0x1001, 2218, 0x8004),
                new TriggerOp(0x1011, 0, 5), new TriggerOp(0x101F, 1134, 9), new TriggerOp(0x10FF, 0, 0),
                new TriggerOp(0x1011, 0, 7),
            };
            var r = MapEditor.RelinkScript(ops, new[] { 5 }, 100);
            Assert.Equal(105, r[0].B);
            Assert.Equal(5, r[2].B);         // the arrival on map 9
            Assert.Equal(7, r[5].B);         // not one of the prefab's links
            Assert.Equal(ops.Skip(1).Take(1), r.Skip(1).Take(1));
        }

        [Fact]
        public void APrefabFromBritanniaPastesOntoAnotherMap()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = Group(ed);
                var p = Prefab.Copy(ed, g, 2, ground: true).Turned(1);
                var dev = Map(4, ed.Scripts);
                int nf = dev.Nonfixed.Objects.Count, fx = dev.Fixed.Objects.Count;
                var r = dev.PastePrefab(p, 100, 120, new PasteOptions());
                Assert.Equal(0, r.Skipped);
                Assert.Equal(g.Count, r.Objects.Count);
                Assert.Equal(nf + g.Count(o => o.Kind == ObjectFileKind.Nonfixed), ObjectMap.Read(dev.Nonfixed.ToBytes(), ObjectFileKind.Nonfixed).Objects.Count);
                Assert.Equal(fx + g.Count(o => o.Kind == ObjectFileKind.Fixed), ObjectMap.Read(dev.Fixed.ToBytes(), ObjectFileKind.Fixed).Objects.Count);
                Assert.True(dev.TerrainChanged);
                var t = TerrainMap.Read(dev.Terrain.ToBytes());
                int ci = p.Width / 2, cj = p.Height / 2;
                Assert.Equal(p.Quads[ci + cj * (p.Width - 1)], t.GetPoint(100 + ci, 120 + cj).Raw & ~0xFFFu);
                Assert.False(ed.HasChanges);         // the source map is untouched
            }
        }
    }
}
