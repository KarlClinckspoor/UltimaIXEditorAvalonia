using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>Where one piece goes: type, map position (object units) and turn about the vertical (degrees).</summary>
    public readonly record struct KitPlacement(int Type, Vector3 Position, float Yaw)
    {
        public Quaternion Rotation => KitBuilding.Upright(Yaw);
        public int MapX => (int)MathF.Round(Position.X);
        public int MapY => (int)MathF.Round(Position.Y);
        public int Z => (int)MathF.Round(Position.Z);
    }

    /// <summary>A snapped spot for a piece, and what it snapped to (for the status line).</summary>
    public readonly record struct KitSnapResult(KitPlacement Placement, MapObject To, string How);

    /// <summary>
    /// Putting kit pieces down the way the original maps were made: next to a piece already placed, at an offset the
    /// source map uses between those two kinds of piece (<see cref="KitCatalog.Joins"/>), else on the placed piece's own
    /// turned grid; and strung along a line for walls.
    /// </summary>
    public static class KitBuilding
    {
        /// <summary>A rotation about the vertical only.</summary>
        public static Quaternion Upright(float yaw) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw * MathF.PI / 180f);

        /// <summary>Degrees in -180..180.</summary>
        public static float Wrap(float a) { a %= 360; return a > 180 ? a - 360 : a <= -180 ? a + 360 : a; }

        /// <summary>
        /// The best spot for a <paramref name="piece"/> near a map position (object units; z the height the cursor
        /// points at): every join the kit knows from a nearby placed piece to this kind (and the other way round) gives
        /// a spot; the nearest within <paramref name="reach"/> horizontally wins. Failing that, the nearest placed piece's
        /// turned grid (the join step) gives one, at that piece's height. Null when nothing is near: the caller places
        /// freely. <paramref name="yaw"/> is the turn the piece would have on its own (the grid keeps its quarter).
        /// </summary>
        public static KitSnapResult? Snap(KitCatalog kit, KitPiece piece, IEnumerable<MapObject> placed, Vector3 cursor, float yaw, float reach)
        {
            var c2 = new Vector2(cursor.X, cursor.Y);
            float look = reach + 2 * Math.Max(piece.Size.X, piece.Size.Y) + 512;
            var near = placed.Where(o => !o.Removed && kit.Pieces.ContainsKey(o.Type)
                                         && Math.Abs(o.MapX - cursor.X) < look && Math.Abs(o.MapY - cursor.Y) < look)
                             .Select(o => (O: o, Yaw: KitCatalog.UprightYaw(o.Rotation)))
                             .Where(x => x.Yaw != null).ToList();
            KitSnapResult? best = null;
            float bestD = reach;
            foreach (var (o, oy) in near)
            {
                var at = new Vector3(o.MapX, o.MapY, o.Z);
                float ny = oy.Value;
                // the new piece where the source map puts this kind after the placed one ...
                foreach (var j in kit.Between(o.Type, piece.Type).Take(12))
                    Consider(at + KitCatalog.ToMap(j.Offset, ny), ny + j.Turn, o, $"join from {Name(kit, o.Type)} x{j.Count}");
                // ... or before it (the join seen from the new piece's side)
                foreach (var j in kit.Between(piece.Type, o.Type).Take(12))
                {
                    float py = ny - j.Turn;
                    Consider(at - KitCatalog.ToMap(j.Offset, py), py, o, $"join to {Name(kit, o.Type)} x{j.Count}");
                }
            }
            if (best != null) return best;

            // the nearest placed piece's own grid
            var nearest = near.OrderBy(x => Vector2.DistanceSquared(new Vector2(x.O.MapX, x.O.MapY), c2)).FirstOrDefault();
            if (nearest.O == null || Vector2.Distance(new Vector2(nearest.O.MapX, nearest.O.MapY), c2) > look) return null;
            var grid = KitGrid.Of(nearest.O, kit.JoinStep);
            var p = grid.Snap(c2);
            return new KitSnapResult(new KitPlacement(piece.Type, new Vector3(p, nearest.O.Z), Wrap(grid.SnapYaw(yaw))), nearest.O,
                                     $"{kit.JoinStep}-unit grid of {Name(kit, nearest.O.Type)}");

            void Consider(Vector3 pos, float y, MapObject to, string how)
            {
                float d = Vector2.Distance(new Vector2(pos.X, pos.Y), c2);
                if (d >= bestD) return;
                // a spot where a piece of this kind already stands is no use
                if (near.Any(x => x.O.Type == piece.Type && Vector2.DistanceSquared(new Vector2(x.O.MapX, x.O.MapY), new Vector2(pos.X, pos.Y)) < 16 * 16)) return;
                bestD = d;
                best = new KitSnapResult(new KitPlacement(piece.Type, pos, Wrap(y)), to, how);
            }
        }

        static string Name(KitCatalog kit, int type) => kit.Pieces.TryGetValue(type, out var p) ? p.Label : "type " + type;

        /// <summary>
        /// The step from one piece to the next in a straight run going <paramref name="along"/> (a unit direction in the
        /// pieces' own frame; null: the run's first step decides): of the kit's same-turn joins between them (either
        /// way round), the one pointing most nearly that way, the commonest of those; else end to end along that axis
        /// (from the bounds: the first's far end to the second's near end).
        /// </summary>
        public static Vector3 Step(KitCatalog kit, int from, int to, Vector2? along = null)
        {
            var dir = along ?? (kit.StraightStep(from, to) is { } first ? Vector2.Normalize(new Vector2(first.Offset.X, first.Offset.Y)) : Vector2.UnitX);
            var options = kit.Between(from, to).Select(j => (j.Offset, j.Count, j.Turn))
                             .Concat(kit.Between(to, from).Select(j => (Offset: -j.Offset, j.Count, j.Turn)))
                             .Where(j => j.Turn == 0 && new Vector2(j.Offset.X, j.Offset.Y).Length() > 1 && Math.Abs(j.Offset.Z) < 0.25f * new Vector2(j.Offset.X, j.Offset.Y).Length())
                             .Select(j => (j.Offset, j.Count, Dot: Vector2.Dot(Vector2.Normalize(new Vector2(j.Offset.X, j.Offset.Y)), dir)))
                             .Where(j => j.Dot > 0.95f)
                             .OrderByDescending(j => j.Count).ToList();
            if (options.Count > 0) return options[0].Offset;
            var a = kit.Pieces[from]; var b = kit.Pieces[to];
            // end to end along the run's axis: x when it is mostly x, else y
            if (Math.Abs(dir.X) >= Math.Abs(dir.Y))
                return dir.X >= 0 ? new Vector3(a.BoxMax.X - b.BoxMin.X, 0, 0) : new Vector3(a.BoxMin.X - b.BoxMax.X, 0, 0);
            return dir.Y >= 0 ? new Vector3(0, a.BoxMax.Y - b.BoxMin.Y, 0) : new Vector3(0, a.BoxMin.Y - b.BoxMax.Y, 0);
        }

        /// <summary>
        /// Pieces strung along a line through <paramref name="points"/> (map units), cycling through
        /// <paramref name="pattern"/>, at height <paramref name="z"/>. The first leg starts at the first point; pieces are
        /// turned so that the run's step lies along the leg; a leg ends with the last piece that does not pass its end
        /// point by more than half a step. Later legs turn round the last piece placed (their first piece is one step
        /// from it, towards the next point). <paramref name="flip"/> runs the pieces the other way round (the other face
        /// out).
        /// </summary>
        public static List<KitPlacement> Wall(KitCatalog kit, IReadOnlyList<int> pattern, IReadOnlyList<Vector2> points, int z, bool flip = false, float snapDegrees = 0, int corner = -1)
        {
            if (corner >= 0 && !kit.Pieces.ContainsKey(corner)) throw new ArgumentException($"Type {corner} is not in the kit of map {kit.Map}");
            float? firstHeading = null;
            float prevYaw = 0;
            if (pattern.Count == 0) throw new ArgumentException("No pieces to build with", nameof(pattern));
            foreach (int t in pattern) if (!kit.Pieces.ContainsKey(t)) throw new ArgumentException($"Type {t} is not in the kit of map {kit.Map}");
            var result = new List<KitPlacement>();
            int k = 0;
            // the run's direction in the pieces' own frame, from the pattern's first step (reversed: the other face out)
            var s0 = Step(kit, pattern[0], pattern[1 % pattern.Count]);
            var local = Vector2.Normalize(new Vector2(s0.X, s0.Y));
            if (flip) local = -local;
            float localAngle = MathF.Atan2(local.Y, local.X) * 180f / MathF.PI;
            for (int leg = 0; leg + 1 < points.Count; leg++)
            {
                // a later leg turns round the last piece placed: it starts there, and its first piece is one step on;
                // with a corner piece, that goes first, one step on along the leg before
                bool turning = leg > 0 && result.Count > 0;
                if (turning && corner >= 0 && result[^1].Type != corner)
                    result.Add(new KitPlacement(corner, result[^1].Position + KitCatalog.ToMap(Step(kit, result[^1].Type, corner, local), prevYaw), Wrap(prevYaw)));
                var a = turning ? new Vector2(result[^1].Position.X, result[^1].Position.Y) : points[leg];
                var b = points[leg + 1];
                var d = b - a;
                float length = d.Length();
                if (length < 1) continue;
                // later legs' headings snap to whole steps from the first leg's (90: square corners)
                float heading = MathF.Atan2(d.Y, d.X) * 180f / MathF.PI;
                if (firstHeading is { } h0 && snapDegrees > 0)
                {
                    heading = h0 + MathF.Round(Wrap(heading - h0) / snapDegrees) * snapDegrees;
                    float hr = heading * MathF.PI / 180f;
                    d = new Vector2(MathF.Cos(hr), MathF.Sin(hr)) * Vector2.Dot(d, new Vector2(MathF.Cos(hr), MathF.Sin(hr)));
                    length = d.Length();
                    if (length < 1) continue;
                }
                firstHeading ??= heading;
                // turn the pieces so that the run's direction lies along the leg
                float yaw = heading - localAngle;
                prevYaw = yaw;
                var pos = turning ? result[^1].Position + KitCatalog.ToMap(Step(kit, result[^1].Type, pattern[k % pattern.Count], local), yaw)
                                  : new Vector3(a, z);
                var dir = d / length;
                float lastAlong = turning ? Vector2.Dot(new Vector2(pos.X, pos.Y) - a, dir) : -1;
                if (turning && lastAlong > length + 1) continue;     // the leg is shorter than one step
                for (int guard = 0; guard < 10000; guard++)
                {
                    int type = pattern[k % pattern.Count];
                    result.Add(new KitPlacement(type, pos, Wrap(yaw)));
                    var step = KitCatalog.ToMap(Step(kit, type, pattern[(k + 1) % pattern.Count], local), yaw);
                    float along = Vector2.Dot(new Vector2(pos.X + step.X, pos.Y + step.Y) - a, dir);
                    float stepLen = new Vector2(step.X, step.Y).Length();
                    k++;
                    // done past the end, or when a step does not go forward
                    if (stepLen < 1 || along <= lastAlong || along > length + stepLen / 2) break;
                    lastAlong = along;
                    pos += step;
                }
            }
            return result;
        }

        /// <summary>
        /// Tiles of <paramref name="type"/> filling the polygon through <paramref name="points"/> (map units), at height
        /// <paramref name="z"/>: on a grid along the first edge (its steps the kit's joins of the tile to itself along
        /// its x and y axes, else its size), starting at the first point, every tile whose middle is inside the polygon.
        /// Later edges' headings snap like a wall's (<paramref name="snapDegrees"/>), so walls drawn along the same points
        /// meet the floor. The tiles are turned along the first edge.
        /// </summary>
        public static List<KitPlacement> Fill(KitCatalog kit, int type, IReadOnlyList<Vector2> points, int z, float snapDegrees = 0)
        {
            if (!kit.Pieces.TryGetValue(type, out var piece)) throw new ArgumentException($"Type {type} is not in the kit of map {kit.Map}");
            var poly = SnappedPolygon(points, snapDegrees);
            var result = new List<KitPlacement>();
            if (poly.Count < 3) return result;
            var e = poly[1] - poly[0];
            float yaw = MathF.Atan2(e.Y, e.X) * 180f / MathF.PI;
            // the tile's steps along its own x and y: a join to itself that way, else its size
            var sx = Step(kit, type, type, Vector2.UnitX);
            var sy = Step(kit, type, type, Vector2.UnitY);
            if (Math.Abs(sx.X) < 1) sx = new Vector3(Math.Max(1, piece.Size.X), 0, 0);
            if (Math.Abs(sy.Y) < 1) sy = new Vector3(0, Math.Max(1, piece.Size.Y), 0);
            float stepX = Math.Abs(sx.X), stepY = Math.Abs(sy.Y);
            // the polygon in the tiles' frame, from the first point
            var local = poly.Select(p => KitCatalog.ToLocal(new Vector3(p - poly[0], 0), yaw)).Select(v => new Vector2(v.X, v.Y)).ToList();
            float minX = local.Min(p => p.X), maxX = local.Max(p => p.X), minY = local.Min(p => p.Y), maxY = local.Max(p => p.Y);
            // the model's middle may be off its origin: place so that the tile's box middle lands on the grid cell's
            var boxMiddle = new Vector2((piece.BoxMin.X + piece.BoxMax.X) / 2, (piece.BoxMin.Y + piece.BoxMax.Y) / 2);
            for (int j = (int)MathF.Floor(minY / stepY); j * stepY < maxY; j++)
                for (int i = (int)MathF.Floor(minX / stepX); i * stepX < maxX; i++)
                {
                    var middle = new Vector2((i + 0.5f) * stepX, (j + 0.5f) * stepY);
                    if (!Inside(local, middle)) continue;
                    var at = KitCatalog.ToMap(new Vector3(middle - boxMiddle, 0), yaw);
                    result.Add(new KitPlacement(type, new Vector3(poly[0] + new Vector2(at.X, at.Y), z), Wrap(yaw)));
                }
            return result;
        }

        /// <summary>
        /// A rectangular room: its first side from <paramref name="a"/> towards <paramref name="b"/>, its depth towards
        /// <paramref name="c"/> (square to the first side). Both sides are made a whole number of wall steps, so the
        /// walls close: with a corner piece, corner to corner is the step from corner to wall, whole wall steps, and the
        /// step from wall to corner (Shame: 192 + 256(n-1) + 192 = 128 + 256n), the corner pieces standing on the four
        /// corners; without, a whole number of walls end to end. Walls run along the four sides (turned along each);
        /// <paramref name="floor"/> tiles (-1: none) fill the inside, which is the rectangle less half the wall's
        /// thickness all round (Shame: 256n, n tiles of 663). <paramref name="flip"/> turns the walls round.
        /// </summary>
        public static List<KitPlacement> Room(KitCatalog kit, int floor, IReadOnlyList<int> pattern, int corner, Vector2 a, Vector2 b, Vector2 c, int z, bool flip = false)
        {
            if (pattern.Count == 0) throw new ArgumentException("No wall pieces", nameof(pattern));
            foreach (int t in pattern.Append(corner).Append(floor).Where(t => t >= 0))
                if (!kit.Pieces.ContainsKey(t)) throw new ArgumentException($"Type {t} is not in the kit of map {kit.Map}");
            var ab = b - a;
            if (ab.Length() < 1) throw new ArgumentException("The first side has no length");
            var u = Vector2.Normalize(ab);
            var v = new Vector2(-u.Y, u.X);
            if (Vector2.Dot(c - a, v) < 0) v = -v;          // the depth goes the side C is on
            // the run's direction in the wall pieces' frame (as for a wall), and the steps along it
            var s0 = Step(kit, pattern[0], pattern[1 % pattern.Count]);
            var local = Vector2.Normalize(new Vector2(s0.X, s0.Y));
            float localAngle = MathF.Atan2(local.Y, local.X) * 180f / MathF.PI;
            float Along(Vector3 s) => Math.Abs(Vector2.Dot(new Vector2(s.X, s.Y), local));
            float wallStep(int k) => Along(Step(kit, pattern[k % pattern.Count], pattern[(k + 1) % pattern.Count], local));
            float toFirst = corner >= 0 ? Along(Step(kit, corner, pattern[0], local)) : 0;
            float fromLast(int n) => corner >= 0 ? Along(Step(kit, pattern[(n - 1) % pattern.Count], corner, local)) : 0;
            // how many walls fit a side best, and the side's length then
            (int N, float Length) Layout(float want)
            {
                int best = 1; float bestLen = 0, bestErr = float.MaxValue;
                for (int n = 1; n < 400; n++)
                {
                    float len = corner >= 0 ? toFirst + Enumerable.Range(0, n - 1).Sum(wallStep) + fromLast(n)
                                            : Enumerable.Range(0, n).Sum(wallStep);
                    float err = Math.Abs(len - want);
                    if (err < bestErr) { best = n; bestLen = len; bestErr = err; }
                    if (len > want + 1024) break;
                }
                return (best, bestLen);
            }
            var (n1, width) = Layout(Vector2.Dot(ab, u));
            var (n2, depth) = Layout(Math.Abs(Vector2.Dot(c - a, v)));
            var corners = new[] { a, a + u * width, a + u * width + v * depth, a + v * depth };
            var centre = a + u * (width / 2) + v * (depth / 2);
            var result = new List<KitPlacement>();
            for (int side = 0; side < 4; side++)
            {
                var from = corners[side];
                var to = corners[(side + 1) % 4];
                var dir = Vector2.Normalize(to - from);
                float heading = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
                float yaw = heading - localAngle;
                // the game draws faces from the front only: the walls' faces go towards the room's middle (flip: away)
                var inward = Vector2.Normalize(centre - (from + to) / 2);
                if (Vector2.Dot(Turn(kit.Pieces[pattern[0]].Facing, yaw), inward) < 0) yaw += 180;
                if (flip) yaw += 180;
                int n = side % 2 == 0 ? n1 : n2;
                if (corner >= 0) result.Add(new KitPlacement(corner, new Vector3(from, z), Wrap(FacingYaw(kit.Pieces[corner], yaw, Vector2.Normalize(centre - from)))));
                float at = corner >= 0 ? toFirst : wallStep(0) / 2;
                for (int k = 0; k < n; k++)
                {
                    result.Add(new KitPlacement(pattern[k % pattern.Count], new Vector3(from + dir * at, z), Wrap(yaw)));
                    at += wallStep(k);
                }
            }
            if (floor >= 0)
            {
                // the inside: less half the wall's thickness (its depth across the run) all round
                var w = kit.Pieces[pattern[0]];
                var across = new Vector2(-local.Y, local.X);
                float half = Math.Abs(Vector2.Dot(new Vector2(w.Size.X, w.Size.Y), across)) / 2;
                var inner = new[] { a + (u + v) * half, a + u * (width - half) + v * half, a + u * (width - half) + v * (depth - half), a + u * half + v * (depth - half) };
                result.AddRange(Fill(kit, floor, inner, z));
            }
            return result;
        }

        /// <summary>A piece-frame direction on the map, for a piece turned <paramref name="yaw"/>.</summary>
        static Vector2 Turn(Vector2 v, float yaw) { var m = KitCatalog.ToMap(new Vector3(v, 0), yaw); return new Vector2(m.X, m.Y); }

        /// <summary>
        /// Of <paramref name="yaw"/> and its quarter turns, the one that faces the piece's sides most towards
        /// <paramref name="towards"/> (a corner piece's open quadrant into the room); <paramref name="yaw"/> when the piece
        /// faces every way alike.
        /// </summary>
        static float FacingYaw(KitPiece piece, float yaw, Vector2 towards)
        {
            if (piece.Facing == Vector2.Zero) return yaw;
            return Enumerable.Range(0, 4).Select(k => yaw + 90 * k).OrderByDescending(y => Vector2.Dot(Turn(piece.Facing, y), towards)).First();
        }

        /// <summary>The points with every edge after the first turned to a whole step of degrees from it (0: as given).</summary>
        static List<Vector2> SnappedPolygon(IReadOnlyList<Vector2> points, float snapDegrees)
        {
            var poly = new List<Vector2>();
            if (points.Count == 0) return poly;
            poly.Add(points[0]);
            float? h0 = null;
            for (int i = 1; i < points.Count; i++)
            {
                var d = points[i] - poly[^1];
                if (d.Length() < 1) continue;
                float h = MathF.Atan2(d.Y, d.X) * 180f / MathF.PI;
                if (h0 is { } first && snapDegrees > 0)
                {
                    h = first + MathF.Round(Wrap(h - first) / snapDegrees) * snapDegrees;
                    var u = new Vector2(MathF.Cos(h * MathF.PI / 180), MathF.Sin(h * MathF.PI / 180));
                    d = u * Vector2.Dot(d, u);
                }
                h0 ??= h;
                poly.Add(poly[^1] + d);
            }
            return poly;
        }

        /// <summary>Whether a point is inside a polygon (even-odd rule).</summary>
        static bool Inside(List<Vector2> poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// A record for a new object of a type in a file: a copy of one of that type in the file (or in
        /// <paramref name="source"/>, the kit's map), with its link and property chain cleared (nonfixed); else a fresh
        /// one with no rotation.
        /// </summary>
        public static byte[] Template(ObjectMap target, ObjectMap source, int type)
        {
            var like = target.Objects.FirstOrDefault(o => !o.Removed && o.Type == type)
                       ?? source?.Objects.FirstOrDefault(o => !o.Removed && o.Type == type && o.Map.RecordSize == target.RecordSize);
            byte[] r = like?.Raw;
            if (r == null || r.Length != target.RecordSize)
            {
                r = new byte[target.RecordSize];
                BitConverter.TryWriteBytes(r.AsSpan(0x0A), (ushort)type);
                BitConverter.TryWriteBytes(r.AsSpan(0x12), (short)32767);
            }
            if (target.Kind == ObjectFileKind.Nonfixed)
            {
                BitConverter.TryWriteBytes(r.AsSpan(0x1A), (ushort)0);
                BitConverter.TryWriteBytes(r.AsSpan(0x1C), 0u);
            }
            return r;
        }

        /// <summary>Adds the pieces to a file (inside an edit the caller opened); returns the new objects.</summary>
        public static List<MapObject> AddAll(ObjectMap target, ObjectMap source, IEnumerable<KitPlacement> plan)
        {
            var made = new List<MapObject>();
            foreach (var p in plan)
            {
                int x = Math.Clamp(p.MapX, 0, target.Width * ObjectMap.ChunkUnits - 1);
                int y = Math.Clamp(p.MapY, 0, target.Height * ObjectMap.ChunkUnits - 1);
                var o = target.Add(Template(target, source, p.Type), null, x, y, Math.Clamp(p.Z, 0, 0xFFFF));
                o.SetRotation(p.Rotation);
                made.Add(o);
            }
            return made;
        }

        /// <summary>Adds the pieces to a file inside one edit (undoable as a whole); returns the new objects.</summary>
        public static List<MapObject> Apply(ObjectMap target, ObjectMap source, IEnumerable<KitPlacement> plan, string description = "build")
        {
            target.BeginEdit(description);
            List<MapObject> made;
            try { made = AddAll(target, source, plan); }
            catch
            {
                var partial = target.EndEdit();
                if (partial != null) target.Undo(partial);
                throw;
            }
            target.EndEdit();
            return made;
        }
    }
}
