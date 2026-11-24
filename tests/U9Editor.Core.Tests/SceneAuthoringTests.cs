using System;
using System.IO;
using System.Linq;
using System.Numerics;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using U9.Formats.Triggers;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class SceneAuthoringTests
    {
        /// <summary>Earth with its trigger scripts read from a copy in a temporary folder (saving writes there, never to the game).</summary>
        static MapEditor Earth(out string dir)
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var opt = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            dir = Path.Combine(Path.GetTempPath(), $"u9scenes-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            File.Copy(Directory.EnumerateFiles(g.StaticFolder, "triggers.flx", opt).First(), Path.Combine(dir, "TRIGGERS.FLX"));
            var t = TerrainMap.Load(Directory.EnumerateFiles(g.StaticFolder, "terrain.14", opt).First());
            var space = new MapSpace(t.Width, t.Height);
            var fx = ObjectMap.Load(Directory.EnumerateFiles(g.StaticFolder, "fixed.14", opt).First(), ObjectFileKind.Fixed);
            var nf = ObjectMap.Load(Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), "nonfixed.14", opt).First(), ObjectFileKind.Nonfixed);
            var ed = new MapEditor(14, fx, nf, SceneObjects.Build(fx, nf, space, null), space, new TerrainRaster(t)) { Terrain = t };
            ed.Scripts = TriggerScripts.Load(Path.Combine(dir, "TRIGGERS.FLX"));
            return ed;
        }

        /// <summary>A trigger zone of the map to copy: its record and property segment (Trigger Area and OO Trigger).</summary>
        static (byte[] Record, byte[] Segment) Zone(MapEditor ed)
        {
            var z = ed.Nonfixed.Objects.First(o => o.Type == 1111 && o.Map.PropertiesOf(o).Any(p => p.Id == PropertySegment.OoTrigger)
                                                  && o.Map.PropertiesOf(o).Any(p => p.Id == PropertySegment.TriggerArea));
            return (z.Raw, ed.Nonfixed.Data.AsSpan(ed.Nonfixed.DataStart + z.PropertyOffset, 16).ToArray());
        }

        static CameraPath SceneFrom(MapEditor ed, MapObject starter) =>
            Cutscenes.Find(ed.Nonfixed, ed.Scripts, ed.Space).FirstOrDefault(p => p.Starter == starter);

        [Fact]
        public void AnOrbitSceneCirclesTheObjectLookingAtIt()
        {
            var ed = Earth(out var dir);
            try
            {
                var (rec, seg) = Zone(ed);
                const float cx = 46.7f, crow = 77.9f, r = 4;
                const int cz = 1800, h = 400;
                var zone = ed.NewOrbitScene(rec, seg, 40 * 128, 70 * 128, ed.GroundZ(40 * 128, 70 * 128), cx, crow, cz, r, h, 12, 24);
                var scene = SceneFrom(ed, zone.Source);
                Assert.NotNull(scene);
                Assert.Equal(13, scene.Shots.Count);                 // a cut to the first point, then twelve glides
                Assert.Equal(500 + 12 * 2000, scene.TotalMs);
                Assert.Equal(scene.Shots[0].Point.Pose.Target, scene.Shots[^1].Point.Pose.Target);   // the lap closes
                Assert.Equal(13, scene.Shots.Select(s => s.Point.Link).Distinct().Count());
                var centre = new Vector2(cx, ed.Space.WorldZ(crow));
                foreach (var s in scene.Shots)
                {
                    var p = s.Point.Pose;
                    var at = new Vector2(p.Target.X, p.Target.Z);
                    Assert.InRange(Vector2.Distance(at, centre), r - 0.02f, r + 0.02f);
                    // looking along the heading from the point passes through the centre
                    var dir2 = new Vector2(MathF.Sin(p.Yaw * MathF.PI / 180), MathF.Cos(p.Yaw * MathF.PI / 180));
                    Assert.InRange(Vector2.Distance(at + dir2 * r, centre), 0, 0.05f);
                    Assert.InRange(p.Pitch, 37, 39);                  // atan(400 / 4 / 32 / 4 points)
                }
                // halfway through a glide the camera is on the chord, still about the radius away
                var mid = scene.Sample(500 + 1000);
                Assert.InRange(Vector2.Distance(new Vector2(mid.Target.X, mid.Target.Z), centre), r * 0.96f, r);

                // saved and read again, the scene is still there
                string nfPath = Path.Combine(dir, "nonfixed.14"), fxPath = Path.Combine(dir, "fixed.14");
                ed.Save(fxPath, nfPath);
                var nf2 = ObjectMap.Load(nfPath, ObjectFileKind.Nonfixed);
                var again = Cutscenes.Find(nf2, TriggerScripts.Load(Path.Combine(dir, "TRIGGERS.FLX")), ed.Space)
                                     .First(p => p.Starter.Type == 1111 && p.Shots.Count == 13);
                Assert.Equal(scene.TotalMs, again.TotalMs);

                // one undo takes it all back
                ed.Undo();
                Assert.Null(SceneFrom(ed, zone.Source));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ASceneGrowsMoveByMove()
        {
            var ed = Earth(out var dir);
            try
            {
                var (rec, seg) = Zone(ed);
                var first = new CameraPose(new Vector3(40, 15, 70), 90, 10, 0.8f);
                var zone = ed.NewScene(rec, seg, 41 * 128, 70 * 128, 1700, first, halfSeconds: 3);
                var scene = SceneFrom(ed, zone.Source);
                Assert.Single(scene.Shots);
                Assert.False(scene.Shots[0].Move.Glides);
                Assert.Equal(1500, scene.TotalMs);
                ed.AddSceneMove(scene, new CameraPose(new Vector3(44, 16, 72), 120, 20, 0.8f), CameraMove.Make(6, true, false, false, false, false));
                scene = SceneFrom(ed, zone.Source);
                Assert.Equal(2, scene.Shots.Count);
                Assert.True(scene.Shots[0].Move.RunsFirst);
                Assert.Equal(1500 + 3000, scene.TotalMs);
                Assert.InRange(scene.Shots[1].Point.Pose.Yaw, 119.5f, 120.5f);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ScriptTextReadsBackEveryShippedScript()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var scripts = TriggerScripts.Load(Directory.EnumerateFiles(g.StaticFolder, "triggers.flx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).First());
            int n = 0;
            for (int slot = 0; slot < scripts.Count; slot++)
            {
                var ops = scripts[slot];
                if (ops.Length == 0 || Array.FindIndex(ops, o => o.Command == TriggerOp.End) != ops.Length - 1 || ops[^1].Op != 0x10FF || ops[^1].A != 0 || ops[^1].B != 0) continue;   // one ends with A 11: the text writes a plain end
                Assert.Equal(ops, TriggerScripts.Parse(TriggerScripts.Format(ops)));
                n++;
            }
            Assert.True(n > 6000, $"only {n} scripts checked");
            Assert.Throws<FormatException>(() => TriggerScripts.Parse("1001 2218"));
            Assert.Throws<FormatException>(() => TriggerScripts.Parse("10G1 1 2"));
        }
    }
}
