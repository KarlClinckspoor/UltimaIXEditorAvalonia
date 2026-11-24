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
    public class CutsceneTests
    {
        static MapEditor Earth()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var opt = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            var t = TerrainMap.Load(Directory.EnumerateFiles(g.StaticFolder, "terrain.14", opt).First());
            var space = new MapSpace(t.Width, t.Height);
            var fx = ObjectMap.Load(Directory.EnumerateFiles(g.StaticFolder, "fixed.14", opt).First(), ObjectFileKind.Fixed);
            var nf = ObjectMap.Load(Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), "nonfixed.14", opt).First(), ObjectFileKind.Nonfixed);
            var objects = SceneObjects.Build(fx, nf, space, null);
            var ed = new MapEditor(14, fx, nf, objects, space, new TerrainRaster(t)) { Terrain = t };
            ed.Scripts = TriggerScripts.Load(Directory.EnumerateFiles(g.StaticFolder, "triggers.flx", opt).First());
            return ed;
        }

        static CameraPath Opening(MapEditor ed) =>
            Cutscenes.Find(ed.Nonfixed, ed.Scripts, ed.Space).First(p => p.Starter.Type == 1094 && p.Starter.Link == 1504);

        [Fact]
        public void CameraMoveParametersDecode()
        {
            var m = new CameraMove(0xA00A);
            Assert.Equal(5000, m.Milliseconds);
            Assert.True(m.Glides);
            Assert.True(m.RunsFirst);
            Assert.False(m.RunsSecond || m.IsLinear || m.Returns);
            Assert.Equal(0x9004, CameraMove.Make(4, glide: true, linear: false, back: false, runFirst: false, runSecond: true).B);
            Assert.Equal(0xA806, CameraMove.Make(6, true, true, false, true, false).B);
            Assert.Equal(20 << 7, new TriggerOp(0x102C, 0, 20).RangeUnits);
            Assert.Equal(200, new TriggerOp(0x102C, 0, 0x8000 | 200).RangeUnits);
            Assert.Equal(TriggerOp.DefaultRange, new TriggerOp(0x102C, 0, 0).RangeUnits);
        }

        [Fact]
        public void AnglesReadBackWhatTheyWrite()
        {
            foreach (var (yaw, pitch) in new[] { (0f, 0f), (90f, 11f), (180f, -5f), (271f, 30f), (45f, 60f) })
            {
                var (y, p) = CameraPoint.Angles(CameraPoint.RotationFor(yaw, pitch));
                Assert.InRange(((y - yaw) % 360 + 540) % 360 - 180, -0.05f, 0.05f);
                Assert.InRange(p - pitch, -0.05f, 0.05f);
            }
            // a model's front faces south: no rotation looks south
            Assert.Equal(180f, CameraPoint.Angles(Quaternion.Identity).Yaw, 3);
        }

        [Fact]
        public void TheOpeningSceneOnEarthGoesIntoTheAvatarsHouse()
        {
            var ed = Earth();
            var scene = Opening(ed);
            // the timer in the house cuts to point 71 outside, then glides east through 72 and 58 to 59 inside
            Assert.Equal(new[] { 71, 71, 72, 58, 59, 59 }, scene.Shots.Select(s => s.Point.Link).ToArray());
            Assert.Equal(16000, scene.TotalMs);
            Assert.False(scene.Shots[0].Move.Glides);
            Assert.True(scene.Shots[2].Move.IsLinear);
            foreach (var s in scene.Shots)
            {
                Assert.InRange(s.Point.Pose.Yaw, 89, 92);            // looking east, the way it moves
                Assert.Equal(100, s.Point.DistanceUnits);
            }
            Assert.True(scene.Shots[^1].Point.Pose.Pitch > scene.Shots[0].Point.Pose.Pitch + 8);   // more and more down
            Assert.True(scene.Shots[^1].Point.Pose.Target.X > 46 && scene.Shots[0].Point.Pose.Target.X < 35);

            // halfway through the straight glide from 72 to 58 the camera is halfway between them
            var mid = scene.Sample(7500 + 1500);
            Assert.InRange(mid.Target.X, 45.2f, 45.4f);
            Assert.Equal(scene.Shots[0].Point.Pose.Target, scene.Sample(100).Target);
            Assert.Equal(scene.Shots[^1].Point.Pose.Target, scene.Sample(60000).Target);
        }

        [Fact]
        public void ChangingAMoveIsAnUndoStepInTheScript()
        {
            var ed = Earth();
            var shot = Opening(ed).Shots[2];
            var before = ed.Scripts[shot.Slot];
            ed.SetCameraMove(shot, CameraMove.Make(20, glide: true, linear: false, back: false, runFirst: true, runSecond: false));
            Assert.True(ed.ScriptsChanged);
            Assert.Equal(21000, Opening(ed).TotalMs);   // 5 s became 10 s
            Assert.Equal(before.Length, ed.Scripts[shot.Slot].Length);
            ed.Undo();
            Assert.Equal(before, ed.Scripts[shot.Slot]);
            Assert.False(ed.HasChanges);
            ed.Redo();
            Assert.Equal(21000, Opening(ed).TotalMs);   // 5 s became 10 s
        }

        [Fact]
        public void APointCanBePutWhereAViewIs()
        {
            var ed = Earth();
            var point = Opening(ed).Shots[^1].Point;
            var so = ed.Objects.Of(point.Object);
            var want = new CameraPose(point.Pose.Target + new Vector3(-1.5f, 0.25f, 0.5f), 75, 20, 1.5f);
            ed.SetCameraPoint(so, want);
            var now = new CameraPoint(point.Object, ed.Space).Pose;
            Assert.InRange(Vector3.Distance(now.Target, want.Target), 0, 0.02f);
            Assert.InRange(now.Yaw - want.Yaw, -0.2f, 0.2f);
            Assert.InRange(now.Pitch - want.Pitch, -0.2f, 0.2f);
            Assert.Equal(192, new CameraPoint(point.Object, ed.Space).DistanceUnits);
            ed.Undo();
            var back = new CameraPoint(point.Object, ed.Space).Pose;
            Assert.Equal(point.Pose.Target, back.Target);
            Assert.Equal(100, new CameraPoint(point.Object, ed.Space).DistanceUnits);
        }
    }
}
