using System;
using System.IO;
using System.Linq;
using System.Numerics;
using U9.Formats.Flx;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class ModelTests
    {
        static GameFolder Game()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            return g;
        }

        [Fact]
        public void EveryModelRecordParses()
        {
            var g = Game();
            using var sap = FlxArchive.Open(Path.Combine(g.StaticFolder, "sappear.flx"));
            int models = 0, other = 0, tris = 0;
            foreach (int i in sap.UsedIndices())
            {
                var r = sap.Read(i);
                if (!SappearModel.IsModel(r)) { other++; continue; }
                tris += SappearModel.Triangles(r, 0).Count;
                models++;
            }
            Assert.True(models >= 3740, $"{models} models, {other} other records");
            Assert.True(tris > 100000);
        }

        [Fact]
        public void ThroneModelFromTypeTable()
        {
            var g = Game();
            var types = TypeTable.Load(Path.Combine(g.StaticFolder, "TYPES.DAT"));
            Assert.Equal(1793, types[2894].DefaultModel);
            using var sap = FlxArchive.Open(Path.Combine(g.StaticFolder, "sappear.flx"));
            var tris = SappearModel.Triangles(sap.Read(1793));
            Assert.NotEmpty(tris);
            // a throne is taller than it is deep, and stands on z = 0
            float minZ = tris.Min(t => Math.Min(t.P0.Z, Math.Min(t.P1.Z, t.P2.Z)));
            float maxZ = tris.Max(t => Math.Max(t.P0.Z, Math.Max(t.P1.Z, t.P2.Z)));
            Assert.InRange(minZ, -20, 20);
            Assert.InRange(maxZ, 100, 600);
        }

        [Fact]
        public void InvisibleFacesAreTheMagentaOnes()
        {
            var g = Game();
            using var sap = FlxArchive.Open(Path.Combine(g.StaticFolder, "sappear.flx"));
            // model 1244 (type 2418, in Moonglow) has a limb of magenta blocking faces
            var tris = SappearModel.Triangles(sap.Read(1244));
            var hidden = tris.Where(t => t.Invisible).ToList();
            Assert.Equal(12, hidden.Count);
            Assert.All(hidden, t => Assert.Equal(0xFF00FFu, t.Colour & 0xFFFFFF));
            Assert.DoesNotContain(tris, t => !t.Invisible && t.Texture < 0 && (t.Colour & 0xFFFFFF) == 0xFF00FF);
        }

        [Fact]
        public void PlacementPutsTheModelOriginAtTheObject()
        {
            var g = Game();
            var m = ObjectMap.Load(Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), "nonfixed.9", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).First(), ObjectFileKind.Nonfixed);
            var throne = m.Objects.Single(o => o.Type == 2894);
            var space = new MapSpace(1024, 1024);
            var p = Vector3.Transform(Vector3.Zero, ModelScene.Placement(throne, space));
            Assert.Equal((float)throne.PointX, p.X, 3);
            Assert.Equal((float)throne.PointRow, p.Z, 3);
            Assert.Equal(MapSpace.WorldYFromObject(throne.Z), p.Y, 3);
            // model "up" (z) stays up in the world
            var up = Vector3.TransformNormal(Vector3.UnitZ, ModelScene.Placement(throne, space));
            Assert.True(up.Y > 0);
        }
    }
}

namespace U9Editor.Core.Tests
{
    public class ModelSceneTests
    {
        [Fact]
        public void BritanniaSceneBuilds()
        {
            var g = GameFolder.FindNear(System.AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var types = TypeTable.Load(Path.Combine(g.StaticFolder, "TYPES.DAT"));
            var fx = ObjectMap.Load(Path.Combine(g.StaticFolder, "fixed.9"), ObjectFileKind.Fixed);
            var nf = ObjectMap.Load(Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), "nonfixed.9", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).First(), ObjectFileKind.Nonfixed);
            var space = new MapSpace(1024, 1024);
            var objs = SceneObjects.Build(fx, nf, space, null);
            using var sap = FlxArchive.Open(Path.Combine(g.StaticFolder, "sappear.flx"));
            using var bmp = FlxArchive.Open(Path.Combine(g.StaticFolder, "bitmap16.flx"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scene = ModelScene.Build(objs, space, types, i => sap.Read(i), i => bmp.Read(i));
            TestContext.Current.SendDiagnosticMessage($"models {scene.Batches.Count}, instances {scene.Batches.Sum(b => b.InstanceCount)}, triangles {scene.Triangles}, layers {scene.Layers.Count}, failed {scene.Failed.Count}, {sw.ElapsedMilliseconds} ms");
            System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "u9scene.txt"),
                $"models {scene.Batches.Count}, instances {scene.Batches.Sum(b => b.InstanceCount)}, triangles {scene.Triangles}, layers {scene.Layers.Count}, failed {string.Join(",", scene.Failed.Keys)}, {sw.ElapsedMilliseconds} ms");
            // two objects on Britannia name empty model slots (6767, 6821)
            Assert.True(scene.Failed.Count <= 2, string.Join(", ", scene.Failed.Keys));
            Assert.True(scene.Batches.Count > 100);
        }
    }
}
