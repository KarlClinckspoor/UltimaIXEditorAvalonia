using System;
using System.IO;
using System.Linq;
using System.Numerics;
using U9.Formats.Flx;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class EditorTests
    {
        sealed class Britannia : IDisposable
        {
            public MapEditor Editor;
            public FlxArchive Sap, Bmp;
            public void Dispose() { Sap.Dispose(); Bmp.Dispose(); }
        }

        static Britannia Open()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var t = TerrainMap.Load(Path.Combine(g.StaticFolder, "terrain.9"));
            var space = new MapSpace(t.Width, t.Height);
            var fx = ObjectMap.Load(Path.Combine(g.StaticFolder, "fixed.9"), ObjectFileKind.Fixed);
            var nf = ObjectMap.Load(Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), "nonfixed.9", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).First(), ObjectFileKind.Nonfixed);
            var objects = SceneObjects.Build(fx, nf, space, null);
            var b = new Britannia
            {
                Sap = FlxArchive.Open(Path.Combine(g.StaticFolder, "sappear.flx")),
                Bmp = FlxArchive.Open(Path.Combine(g.StaticFolder, "bitmap16.flx")),
            };
            var types = TypeTable.Load(Path.Combine(g.StaticFolder, "TYPES.DAT"));
            b.Editor = new MapEditor(9, fx, nf, objects, space, new TerrainRaster(t)) { Types = types };
            b.Editor.Scripts = U9.Formats.Triggers.TriggerScripts.Load(Path.Combine(g.StaticFolder, "TRIGGERS.FLX"));
            b.Editor.Models = ModelScene.Build(objects, space, types, i => b.Sap.Read(i), i => b.Bmp.Read(i));
            return b;
        }

        static Vector3 InstanceOrigin(ModelScene m, int id)
        {
            int k = m.SlotOf(id) * ModelBatch.InstanceFloats;
            return new Vector3(m.Instances[k + 12], m.Instances[k + 13], m.Instances[k + 14]);
        }

        [Fact]
        public void MovingTheThroneMovesItsModelAndUndoPutsItBack()
        {
            using var b = Open();
            var ed = b.Editor;
            var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
            var start = throne.World;
            int x = throne.Source.MapX, y = throne.Source.MapY, z = throne.Source.Z;
            bool? structural = null;
            ed.Changed += (t, s) => structural = s;

            ed.Move(throne, x + 5 * 128, y + 40 * 128, z);       // forty points north: another chunk
            Assert.False(structural);
            Assert.True(ed.NonfixedChanged);
            Assert.Equal(start + new Vector3(5, 0, 40), throne.World);
            Assert.Equal(throne.World, InstanceOrigin(ed.Models, throne.Id), new Vec3Comparer());

            ed.Undo();
            Assert.False(ed.HasChanges);
            Assert.Equal(start, throne.World);
            Assert.Equal(start, InstanceOrigin(ed.Models, throne.Id), new Vec3Comparer());
        }

        [Fact]
        public void CopyAndDeleteRegroupTheInstances()
        {
            using var b = Open();
            var ed = b.Editor;
            var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
            int instances = ed.Models.Instances.Length;
            bool? structural = null;
            ed.Changed += (t, s) => structural = s;

            var copy = ed.Duplicate(throne, throne.Source.MapX + 128, throne.Source.MapY, throne.Source.Z);
            Assert.True(structural);
            Assert.Equal(instances + ModelBatch.InstanceFloats, ed.Models.Instances.Length);
            Assert.Equal(throne.Model, copy.Model);

            ed.Remove(throne);
            Assert.Equal(instances, ed.Models.Instances.Length);
            Assert.Equal(-1, ed.Models.SlotOf(throne.Id));

            ed.Undo();
            ed.Undo();
            Assert.Equal(instances, ed.Models.Instances.Length);
            Assert.True(copy.Source.Removed);
            Assert.True(ed.Models.SlotOf(throne.Id) >= 0);
            Assert.False(ed.HasChanges);
        }

        [Fact]
        public void ARayDownOntoTheThroneHitsIt()
        {
            using var b = Open();
            var ed = b.Editor;
            var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
            var origin = throne.BoundCentre + new Vector3(0, 50, 0);
            var hit = ed.Objects.PickModel(ed.Models, ed.Space, origin, -Vector3.UnitY, float.PositiveInfinity, o => o == throne, out float t);   // the castle roof is above it
            Assert.NotNull(hit);
            Assert.InRange(t, 45, 55);
        }

        [Fact]
        public void SavingWritesTheMoveAndKeepsTheOriginal()
        {
            using var b = Open();
            var ed = b.Editor;
            string dir = Path.Combine(Path.GetTempPath(), "u9edit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string fx = Path.Combine(dir, "fixed.9"), nf = Path.Combine(dir, "nonfixed.9");
                File.WriteAllBytes(fx, ed.Fixed.Data);
                File.WriteAllBytes(nf, ed.Nonfixed.Data);
                var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
                int x = throne.Source.MapX + 3 * 128, y = throne.Source.MapY - 2 * 128, z = throne.Source.Z + 40;
                ed.Move(throne, x, y, z, MapEditor.Turned(throne.Source.Rotation, 90));
                var written = ed.Save(fx, nf);
                Assert.Equal(new[] { nf }, written);
                Assert.False(ed.HasChanges);
                Assert.True(File.Exists(nf + ".orig"));
                Assert.False(File.Exists(fx + ".orig"));
                var back = ObjectMap.Load(nf, ObjectFileKind.Nonfixed).Objects.Single(o => o.Type == 2894);
                Assert.Equal((x, y, z), (back.MapX, back.MapY, back.Z));
                Assert.InRange(MathF.Abs(MapEditor.Yaw(back.Rotation) - MapEditor.Yaw(throne.Source.Rotation)), 0, 0.1f);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PickingIsQuickEnoughForHovering()
        {
            using var b = Open();
            var ed = b.Editor;
            var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
            var cam = new OrbitCamera { Target = throne.World, Distance = 60, Pitch = 45 };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int hits = 0, n = 0;
            for (int py = 0; py < 900; py += 60)
                for (int px = 0; px < 1400; px += 70, n++)
                {
                    cam.Ray(px, py, 1400, 900, out var o, out var d);
                    if (ed.Objects.PickModel(ed.Models, ed.Space, o, d, float.PositiveInfinity, null, out _) != null) hits++;
                }
            double ms = sw.Elapsed.TotalMilliseconds / n;
            TestContext.Current.SendDiagnosticMessage($"{ms:F2} ms per pick, {hits} of {n} hit");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "u9pick.txt"), $"{ms:F2} ms per pick, {hits} of {n} hit");
            Assert.True(hits > n / 4);
            Assert.True(ms < 15, $"{ms:F2} ms per pick");
        }

        sealed class Vec3Comparer : System.Collections.Generic.IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
            public int GetHashCode(Vector3 v) => 0;
        }
    }
}
