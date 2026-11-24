using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class GroupEditTests
    {
        static MapEditor Britannia(out IDisposable keep)
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            keep = (IDisposable)b;
            return (MapEditor)b.GetType().GetField("Editor").GetValue(b);
        }

        // a few fixed and nonfixed objects near Castle British's throne, all free to leave their chunk
        static List<SceneObject> Group(MapEditor ed)
        {
            var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
            var near = ed.Objects.All.Where(o => !o.Source.Removed && o.Source.Map.CanLeaveChunk(o.Source) &&
                                                 Math.Abs(o.Source.MapX - throne.Source.MapX) < 12 * 128 && Math.Abs(o.Source.MapY - throne.Source.MapY) < 12 * 128).ToList();
            var g = near.Where(o => o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed).Take(3)
                        .Concat(near.Where(o => o.Kind == U9.Formats.Objects.ObjectFileKind.Nonfixed).Take(3)).ToList();
            Assert.Equal(6, g.Count);
            return g;
        }

        static List<(int, int, int, System.Numerics.Quaternion)> Where(List<SceneObject> g) =>
            g.Select(o => (o.Source.MapX, o.Source.MapY, o.Source.Z, o.Source.Rotation)).ToList();

        [Fact]
        public void MovingAGroupOfBothFilesIsOneStepAndUndoPutsAllBack()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = Group(ed);
                var before = Where(g);
                ed.MoveGroup(g, 3 * 128, -2 * 128, 0, keepAboveGround: false);
                Assert.Equal("move 6 objects", ed.UndoDescription);
                Assert.True(ed.FixedChanged && ed.NonfixedChanged);
                for (int i = 0; i < g.Count; i++)
                {
                    Assert.Equal(before[i].Item1 + 384, g[i].Source.MapX);
                    Assert.Equal(before[i].Item2 - 256, g[i].Source.MapY);
                    Assert.Equal(before[i].Item3, g[i].Source.Z);
                }
                ed.Undo();
                Assert.False(ed.HasChanges);
                Assert.Equal(before, Where(g));
                ed.Redo();
                Assert.Equal(before[0].Item1 + 384, g[0].Source.MapX);
            }
        }

        [Fact]
        public void TurningAGroupGoesRoundItsCentreAndTurnsEachObject()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = Group(ed);
                var before = Where(g);
                var (cx, cy) = MapEditor.GroupCentre(g);
                ed.TurnGroup(g, 90);
                for (int i = 0; i < g.Count; i++)
                {
                    // a quarter turn counter-clockwise: east of the centre goes north
                    float rx = before[i].Item1 - cx, ry = before[i].Item2 - cy;
                    Assert.InRange(g[i].Source.MapX - (cx - ry), -1.01f, 1.01f);
                    Assert.InRange(g[i].Source.MapY - (cy + rx), -1.01f, 1.01f);
                    float turned = MapEditor.Yaw(g[i].Source.Rotation) - MapEditor.Yaw(before[i].Item4);
                    Assert.InRange(((turned % 360) + 360 + 180) % 360 - 180, 89f, 91f);
                }
                ed.TurnGroup(g, -90);
                ed.Undo(); ed.Undo();
                Assert.Equal(before, Where(g));
            }
        }

        [Fact]
        public void CopyingAndDeletingAGroup()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = Group(ed);
                int count = ed.Objects.All.Count(o => !o.Source.Removed);
                var copies = ed.DuplicateGroup(g, 2 * 128, 0);
                Assert.Equal(6, copies.Count);
                Assert.Equal(count + 6, ed.Objects.All.Count(o => !o.Source.Removed));
                Assert.All(copies.Zip(g), p => Assert.Equal(p.Second.Source.Type, p.First.Source.Type));
                Assert.All(copies.Zip(g), p => Assert.Equal(p.Second.Source.MapX + 256, p.First.Source.MapX));
                ed.RemoveGroup(copies.Concat(g));
                Assert.Equal("delete 12 objects", ed.UndoDescription);
                Assert.Equal(count - 6, ed.Objects.All.Count(o => !o.Source.Removed));
                ed.Undo();
                ed.Undo();
                Assert.Equal(count, ed.Objects.All.Count(o => !o.Source.Removed));
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void AGroupMoveThatOneObjectCannotMakeChangesNothing()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var stuck = ed.Objects.All.FirstOrDefault(o => !o.Source.Removed && !o.Source.Map.CanLeaveChunk(o.Source));
                Assert.SkipWhen(stuck == null, "no object chained to its chunk");
                var g = Group(ed).Append(stuck).ToList();
                var before = Where(g);
                Assert.Throws<InvalidOperationException>(() => ed.MoveGroup(g, 40 * 128, 0, 0, false));
                Assert.Equal(before, Where(g));
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void RaisingAndGroundingAGroup()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = Group(ed);
                var before = Where(g);
                ed.MoveGroup(g, 0, 0, 16, false);
                Assert.Equal("raise 6 objects", ed.UndoDescription);
                Assert.All(g.Zip(before), p => Assert.Equal(p.Second.Item3 + 16, p.First.Source.Z));
                ed.DropGroupToGround(g);
                Assert.All(g, o => Assert.Equal(ed.GroundZ(o.Source.MapX, o.Source.MapY), o.Source.Z));
                ed.Undo(); ed.Undo();
                Assert.Equal(before, Where(g));
            }
        }
    }
}
