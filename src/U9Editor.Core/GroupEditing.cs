using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>
    /// Edits on several objects at once (both object files): each is one undo step.
    /// </summary>
    public sealed partial class MapEditor
    {
        /// <summary>Runs an edit over both object files as one undo step; on an error neither file keeps a part of it.</summary>
        T ApplyGroup<T>(string description, Func<T> action)
        {
            var maps = new[] { Fixed, Nonfixed }.Where(m => m != null).ToList();
            foreach (var m in maps) m.BeginEdit(description);
            T result;
            try { result = action(); }
            catch
            {
                foreach (var m in maps) { var partial = m.EndEdit(); if (partial != null) m.Undo(partial); }
                throw;
            }
            var edits = new List<(ObjectMap, ObjectEdit)>();
            foreach (var m in maps)
            {
                var e = m.EndEdit();
                if (e != null) edits.Add((m, e));
            }
            if (edits.Count > 0)
            {
                undo.Add(new Step { Description = description, Follow = edits });
                redo.Clear();
                foreach (var (m, e) in edits) { Bump(m, +1); Publish(e); }
            }
            return result;
        }

        static string Count(int n) => n == 1 ? "1 object" : $"{n} objects";

        static List<SceneObject> Live(IEnumerable<SceneObject> objects) =>
            objects.Where(o => o != null && !o.Source.Removed).Distinct().ToList();

        int ClampX(MapObject o, int x) => Math.Clamp(x, 0, o.Map.Width * ObjectMap.ChunkUnits - 1);
        int ClampY(MapObject o, int y) => Math.Clamp(y, 0, o.Map.Height * ObjectMap.ChunkUnits - 1);

        static bool LeavesChunk(MapObject o, int x, int y) =>
            x / ObjectMap.ChunkUnits != o.ChunkX / ObjectMap.ChunkUnits || y / ObjectMap.ChunkUnits != o.ChunkY / ObjectMap.ChunkUnits;

        void CheckChunks(IEnumerable<(MapObject O, int X, int Y)> moves)
        {
            int stuck = moves.Count(m => !m.O.Map.CanLeaveChunk(m.O) && LeavesChunk(m.O, m.X, m.Y));
            if (stuck > 0)
                throw new InvalidOperationException($"{Count(stuck)} would leave {(stuck == 1 ? "its" : "their")} 32-point chunk, " +
                                                    "and their properties chain on to another segment, so they can only move inside it.");
        }

        /// <summary>
        /// Where each object of a group moved by (dx, dy, dz) object units would go. With <paramref name="keepAboveGround"/>
        /// each keeps its own height above the terrain (then dz is added to that).
        /// </summary>
        public List<(SceneObject Object, int X, int Y, int Z)> GroupMoveTargets(IEnumerable<SceneObject> objects, int dx, int dy, int dz, bool keepAboveGround)
        {
            var list = new List<(SceneObject, int, int, int)>();
            foreach (var o in Live(objects))
            {
                var s = o.Source;
                int x = ClampX(s, s.MapX + dx), y = ClampY(s, s.MapY + dy);
                int z = keepAboveGround ? s.Z - GroundZ(s.MapX, s.MapY) + GroundZ(x, y) + dz : s.Z + dz;
                list.Add((o, x, y, Math.Clamp(z, 0, 0xFFFF)));
            }
            return list;
        }

        /// <summary>Moves a group by (dx, dy, dz) object units; one undo step.</summary>
        public void MoveGroup(IEnumerable<SceneObject> objects, int dx, int dy, int dz, bool keepAboveGround)
        {
            var targets = GroupMoveTargets(objects, dx, dy, dz, keepAboveGround);
            if (targets.Count == 0) return;
            CheckChunks(targets.Select(t => (t.Object.Source, t.X, t.Y)));
            string what = dx == 0 && dy == 0 ? (dz > 0 ? "raise" : "lower") : "move";
            ApplyGroup($"{what} {Count(targets.Count)}", () =>
            {
                foreach (var (o, x, y, z) in targets)
                    if (x != o.Source.MapX || y != o.Source.MapY || z != o.Source.Z) o.Source.MoveTo(x, y, z);
                return 0;
            });
        }

        /// <summary>Puts each object of a group on the ground under it; one undo step.</summary>
        public void DropGroupToGround(IEnumerable<SceneObject> objects)
        {
            var list = Live(objects);
            if (list.Count == 0) return;
            ApplyGroup($"put {Count(list.Count)} on the ground", () =>
            {
                foreach (var o in list)
                {
                    int z = Math.Clamp(GroundZ(o.Source.MapX, o.Source.MapY), 0, 0xFFFF);
                    if (z != o.Source.Z) o.Source.MoveTo(o.Source.MapX, o.Source.MapY, z);
                }
                return 0;
            });
        }

        /// <summary>The middle of a group (object units across the map): the centre of its bounding rectangle.</summary>
        public static (float X, float Y) GroupCentre(IEnumerable<SceneObject> objects)
        {
            var l = Live(objects);
            if (l.Count == 0) return (0, 0);
            return ((l.Min(o => o.Source.MapX) + l.Max(o => o.Source.MapX)) / 2f, (l.Min(o => o.Source.MapY) + l.Max(o => o.Source.MapY)) / 2f);
        }

        /// <summary>
        /// Turns a group about its centre by <paramref name="degrees"/> (counter-clockwise seen from above): positions go
        /// round the centre and every object turns with them, as one undo step. Objects keep their height above the ground.
        /// </summary>
        public void TurnGroup(IEnumerable<SceneObject> objects, float degrees)
        {
            var list = Live(objects);
            if (list.Count == 0) return;
            var (cx, cy) = GroupCentre(list);
            float a = degrees * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
            var targets = new List<(SceneObject O, int X, int Y, int Z)>();
            foreach (var o in list)
            {
                float rx = o.Source.MapX - cx, ry = o.Source.MapY - cy;
                int x = ClampX(o.Source, (int)MathF.Round(cx + rx * c - ry * s)), y = ClampY(o.Source, (int)MathF.Round(cy + rx * s + ry * c));
                int z = Math.Clamp(o.Source.Z - GroundZ(o.Source.MapX, o.Source.MapY) + GroundZ(x, y), 0, 0xFFFF);
                targets.Add((o, x, y, z));
            }
            CheckChunks(targets.Select(t => (t.O.Source, t.X, t.Y)));
            ApplyGroup($"turn {Count(list.Count)}", () =>
            {
                foreach (var (o, x, y, z) in targets)
                {
                    if (x != o.Source.MapX || y != o.Source.MapY || z != o.Source.Z) o.Source.MoveTo(x, y, z);
                    o.Source.SetRotation(Turned(o.Source.Rotation, degrees));
                }
                return 0;
            });
        }

        /// <summary>Copies a group, moved by (dx, dy) object units (keeping heights above the ground); returns the copies.</summary>
        public List<SceneObject> DuplicateGroup(IEnumerable<SceneObject> objects, int dx, int dy)
        {
            var targets = GroupMoveTargets(objects, dx, dy, 0, keepAboveGround: true);
            if (targets.Count == 0) return new List<SceneObject>();
            var copies = ApplyGroup($"copy {Count(targets.Count)}", () =>
                targets.Select(t => t.Object.Source.Map.Duplicate(t.Object.Source, t.X, t.Y, t.Z)).ToList());
            return copies.Select(c => Objects.Of(c)).Where(c => c != null).ToList();
        }

        /// <summary>Deletes a group; one undo step.</summary>
        public void RemoveGroup(IEnumerable<SceneObject> objects)
        {
            var list = Live(objects);
            if (list.Count == 0) return;
            ApplyGroup($"delete {Count(list.Count)}", () =>
            {
                foreach (var o in list) o.Source.Map.Remove(o.Source);
                return 0;
            });
        }
    }
}
