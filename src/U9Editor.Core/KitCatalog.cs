using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using U9.Formats.Models;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>One kind of piece a map is built from: a type, its model and the model's bounds (object units).</summary>
    public sealed class KitPiece
    {
        public int Type { get; init; }
        public string Name { get; init; }
        public int Model { get; init; }
        /// <summary>How many of it the source map has.</summary>
        public int Count { get; internal set; }
        public Vector3 BoxMin { get; init; }
        public Vector3 BoxMax { get; init; }
        public Vector3 Size => BoxMax - BoxMin;
        /// <summary>
        /// Which way the model's sides face, in its own frame: the area-weighted mean of its faces' horizontal normals,
        /// unit length; zero when they balance out (a piece seen from all round). The game draws faces from their front
        /// only: Shame's wall 666 has one drawn side, facing +y in its own frame (Facing (0, 1)); its corner pillar 664
        /// two, facing -x and +y (Facing (-1, 1)/√2). Checked in the game: a room built so faces point inwards shows
        /// all its walls and closed corners.
        /// </summary>
        public Vector2 Facing { get; init; }
        /// <summary>The joins from this piece to others (or to itself), most used first.</summary>
        public List<KitJoin> Joins { get; } = new List<KitJoin>();
        public string Label => Name != null ? $"{Name} ({Type})" : $"type {Type}";
        public override string ToString() => Label;
    }

    /// <summary>
    /// A recurring placement of one piece next to another in the source map: a piece of type <see cref="To"/> sits at
    /// <see cref="Offset"/> from a piece of type <see cref="From"/>, measured in the From piece's own frame (its x axis
    /// is its model's x axis; object units), turned <see cref="Turn"/> degrees more (0, 90, 180 or 270).
    /// </summary>
    public readonly record struct KitJoin(int From, int To, Vector3 Offset, int Turn, int Count)
    {
        /// <summary>Length of the horizontal part of the offset.</summary>
        public float Reach => new Vector2(Offset.X, Offset.Y).Length();
    }

    /// <summary>
    /// A grid turned <see cref="Angle"/> degrees (counter-clockwise, map axes) with cells of <see cref="Step"/> object
    /// units and lines through <see cref="Origin"/>. Maps are put together from sections each laid on its own turned
    /// grid (Shame has pieces at -10, -30, 35, 15 and 0 degrees, and sections turned alike have different origins), so
    /// the useful grid is the local one of a piece already placed (<see cref="Of"/>).
    /// </summary>
    public readonly record struct KitGrid(float Angle, Vector2 Origin, int Step)
    {
        /// <summary>The local grid of one placed piece: its turn (modulo 90) and position, cells of <paramref name="step"/>.</summary>
        public static KitGrid Of(MapObject o, int step)
        {
            float yaw = KitCatalog.UprightYaw(o.Rotation) ?? 0;
            float a = ((yaw % 90) + 90) % 90;
            if (a >= 45) a -= 90;
            return new KitGrid(a, new Vector2(o.MapX, o.MapY), step);
        }

        /// <summary>The nearest grid point (and the height left as it is) to a map position.</summary>
        public Vector2 Snap(Vector2 p, int step = 0)
        {
            int s = step > 0 ? step : Step;
            var l = KitCatalog.ToLocal(new Vector3(p - Origin, 0), Angle);
            var q = new Vector3(MathF.Round(l.X / s) * s, MathF.Round(l.Y / s) * s, 0);
            var m = KitCatalog.ToMap(q, Angle);
            return Origin + new Vector2(m.X, m.Y);
        }

        /// <summary>The nearest turn that is the grid's angle plus a whole number of <paramref name="degrees"/>.</summary>
        public float SnapYaw(float yaw, float degrees = 90) => Angle + MathF.Round((yaw - Angle) / degrees) * degrees;
    }

    /// <summary>
    /// The kit a map is built from: the pieces it uses and how they join. U9's dungeons are a few dozen to a few hundred
    /// piece types, reused (only about a third once), placed off any grid at any angle; walls are strung end to end
    /// (in Shame, wall 666 follows wall 666 256 units along its own axis, and pillar 664 sits 192 from it; 663 is a
    /// floor-and-ceiling tile). Joins are found
    /// by looking at every pair of nearby pieces turned alike (or a quarter turn apart), in the first piece's frame, and
    /// keeping the offsets that recur.
    /// </summary>
    public sealed class KitCatalog
    {
        public int Map { get; init; }
        public Dictionary<int, KitPiece> Pieces { get; } = new Dictionary<int, KitPiece>();
        public List<KitJoin> Joins { get; } = new List<KitJoin>();

        /// <summary>
        /// The grid step the joins imply: the largest of 256, 128, 64, 32, 16 that divides the horizontal offsets of
        /// the joins making up at least 80% of the join count (64 when there are no joins).
        /// </summary>
        public int JoinStep
        {
            get
            {
                int total = Joins.Sum(j => j.Count);
                if (total == 0) return 64;
                foreach (int s in new[] { 256, 128, 64, 32, 16 })
                {
                    bool Fits(float v) { float r = ((v % s) + s) % s; return r <= 2 || r >= s - 2; }
                    int fit = Joins.Where(j => Fits(j.Offset.X) && Fits(j.Offset.Y)).Sum(j => j.Count);
                    if (fit >= 0.8f * total) return s;
                }
                return 16;
            }
        }

        /// <summary>Joins found fewer times than this are dropped.</summary>
        public const int MinJoinCount = 3;
        /// <summary>Offsets are counted in buckets of this many object units, then averaged.</summary>
        const float Bucket = 8f;
        /// <summary>A turn within this many degrees of a quarter counts as that quarter.</summary>
        const float TurnTolerance = 3f;

        public IEnumerable<KitPiece> ByUse => Pieces.Values.OrderByDescending(p => p.Count).ThenBy(p => p.Type);

        /// <summary>Joins from one type to another, most used first.</summary>
        public IEnumerable<KitJoin> Between(int from, int to) => Joins.Where(j => j.From == from && j.To == to).OrderByDescending(j => j.Count);

        /// <summary>
        /// The kit of a map's objects (usually its fixed file: walls, floors, ceilings). <paramref name="readModel"/>
        /// reads a sappear.flx record; <paramref name="name"/> may return null.
        /// </summary>
        public static KitCatalog Build(int map, IEnumerable<MapObject> objects, TypeTable types, Func<int, byte[]> readModel, Func<int, string> name)
        {
            var cat = new KitCatalog { Map = map };
            var list = objects.Where(o => !o.Removed && o.Type != TypeTable.PlaceholderType).ToList();
            var boxes = new Dictionary<int, (Vector3 Min, Vector3 Max, Vector2 Facing)?>();
            foreach (var o in list)
            {
                if (cat.Pieces.TryGetValue(o.Type, out var p)) { p.Count++; continue; }
                int model = o.Model >= 0 ? o.Model : types[o.Type].DefaultModel;
                if (!boxes.TryGetValue(model, out var box)) boxes[model] = box = Shape(model, readModel);
                cat.Pieces[o.Type] = new KitPiece
                {
                    Type = o.Type, Name = name?.Invoke(o.Type), Model = model, Count = 1,
                    BoxMin = box?.Min ?? Vector3.Zero, BoxMax = box?.Max ?? Vector3.Zero, Facing = box?.Facing ?? Vector2.Zero,
                };
            }
            cat.FindJoins(list);
            return cat;
        }

        /// <summary>A model's bounds (visible triangles, object units), or null.</summary>
        public static (Vector3 Min, Vector3 Max)? Bounds(int model, Func<int, byte[]> readModel) =>
            Shape(model, readModel) is { } s ? (s.Min, s.Max) : null;

        /// <summary>A model's bounds and which way its sides face (<see cref="KitPiece.Facing"/>), or null.</summary>
        public static (Vector3 Min, Vector3 Max, Vector2 Facing)? Shape(int model, Func<int, byte[]> readModel)
        {
            if (model <= 0) return null;
            try
            {
                var rec = readModel(model);
                if (rec == null || !SappearModel.IsModel(rec)) return null;
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                var facing = Vector2.Zero;
                float sides = 0;
                foreach (var t in SappearModel.Triangles(rec, 0))
                {
                    if (t.Invisible) continue;
                    min = Vector3.Min(min, Vector3.Min(t.P0, Vector3.Min(t.P1, t.P2)));
                    max = Vector3.Max(max, Vector3.Max(t.P0, Vector3.Max(t.P1, t.P2)));
                    // area-weighted normal of the side the game draws: its fronts wind the other way round
                    // (checked in the game: walls turned by the counter-clockwise normal showed their backs, unseen)
                    var n = Vector3.Cross(t.P2 - t.P0, t.P1 - t.P0) / 2;
                    var h = new Vector2(n.X, n.Y);
                    facing += h;
                    sides += h.Length();
                }
                if (min.X > max.X) return null;
                // a direction only when the sides lean clearly one way (a fifth of their area or more)
                var f = sides > 0 && facing.Length() >= 0.2f * sides ? Vector2.Normalize(facing) : Vector2.Zero;
                return (min, max, f);
            }
            catch (System.IO.InvalidDataException) { return null; }
        }

        /// <summary>An object's turn about the vertical, or null when it is tilted (joins only consider upright pieces).</summary>
        public static float? UprightYaw(Quaternion q)
        {
            if (q.LengthSquared() < 1e-6f) return 0f;
            q = Quaternion.Normalize(q);
            var up = Vector3.Transform(Vector3.UnitZ, q);
            if (up.Z < 0.999f) return null;
            return MapEditor.Yaw(q);
        }

        /// <summary>A map-space offset (x east, y the map's rows) seen from a piece turned <paramref name="yaw"/> degrees.</summary>
        public static Vector3 ToLocal(Vector3 d, float yaw)
        {
            float a = -yaw * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
            return new Vector3(d.X * c - d.Y * s, d.X * s + d.Y * c, d.Z);
        }

        /// <summary>A piece-frame offset back to map space.</summary>
        public static Vector3 ToMap(Vector3 local, float yaw) => ToLocal(local, -yaw);

        void FindJoins(List<MapObject> list)
        {
            // pieces with a usable size, upright; a spatial hash of 1024-unit cells
            var items = new List<(MapObject O, float Yaw, KitPiece P, Vector3 Pos)>();
            foreach (var o in list)
            {
                var yaw = UprightYaw(o.Rotation);
                if (yaw == null || !Pieces.TryGetValue(o.Type, out var p) || p.Size.X <= 0) continue;
                items.Add((o, yaw.Value, p, new Vector3(o.MapX, o.MapY, o.Z)));
            }
            const int Cell = 1024;
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < items.Count; i++)
            {
                var key = ((int)items[i].Pos.X / Cell, (int)items[i].Pos.Y / Cell);
                if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = new List<int>();
                cell.Add(i);
            }
            var counts = new Dictionary<(int From, int To, int Turn, int Bx, int By, int Bz), (int N, Vector3 Sum)>();
            for (int i = 0; i < items.Count; i++)
            {
                var a = items[i];
                float reachA = new Vector2(a.P.Size.X, a.P.Size.Y).Length();
                int cx = (int)a.Pos.X / Cell, cy = (int)a.Pos.Y / Cell;
                for (int gx = cx - 2; gx <= cx + 2; gx++)
                    for (int gy = cy - 2; gy <= cy + 2; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out var cell)) continue;
                        foreach (int j in cell)
                        {
                            if (j == i) continue;
                            var b = items[j];
                            var d = b.Pos - a.Pos;
                            float reach = 0.6f * (reachA + new Vector2(b.P.Size.X, b.P.Size.Y).Length());
                            if (Math.Abs(d.X) > reach || Math.Abs(d.Y) > reach || new Vector2(d.X, d.Y).Length() > Math.Min(reach, 2048)) continue;
                            float turn = ((b.Yaw - a.Yaw) % 360 + 360) % 360;
                            int quarter = (int)MathF.Round(turn / 90f) % 4;
                            float off = Math.Abs(turn - quarter * 90f);
                            if (off > TurnTolerance && off < 360 - TurnTolerance) continue;
                            var local = ToLocal(d, a.Yaw);
                            var key = (a.O.Type, b.O.Type, quarter * 90, (int)MathF.Round(local.X / Bucket), (int)MathF.Round(local.Y / Bucket), (int)MathF.Round(local.Z / Bucket));
                            counts.TryGetValue(key, out var c);
                            counts[key] = (c.N + 1, c.Sum + local);
                        }
                    }
            }
            // merge each peak with its neighbouring buckets (one bucket either way), strongest first
            var used = new HashSet<(int, int, int, int, int, int)>();
            foreach (var kv in counts.OrderByDescending(k => k.Value.N))
            {
                if (used.Contains(kv.Key)) continue;
                var (f, t, turn, bx, by, bz) = kv.Key;
                int n = 0; var sum = Vector3.Zero;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            var k = (f, t, turn, bx + dx, by + dy, bz + dz);
                            if (used.Contains(k) || !counts.TryGetValue(k, out var c)) continue;
                            used.Add(k);
                            n += c.N; sum += c.Sum;
                        }
                if (n < MinJoinCount) continue;
                var avg = sum / n;
                Joins.Add(new KitJoin(f, t, new Vector3(Clean(avg.X), Clean(avg.Y), Clean(avg.Z)), turn, n));
            }
            Joins.Sort((x, y) => y.Count.CompareTo(x.Count));
            foreach (var j in Joins) if (Pieces.TryGetValue(j.From, out var p)) p.Joins.Add(j);
        }

        /// <summary>
        /// An averaged offset, whole; onto the nearest multiple of 16 when within 2 of one. The originals put pieces
        /// 255 or 256 apart for the same join (a unit of jitter, it seems), and their kits are built on 16-unit steps.
        /// </summary>
        static float Clean(float v)
        {
            float g = MathF.Round(v / 16) * 16;
            float r = Math.Abs(v - g) <= 2 ? g : MathF.Round(v);
            return r == 0 ? 0 : r;      // no negative zero ("-0" in listings)
        }

        /// <summary>
        /// The join to use when piece <paramref name="to"/> follows <paramref name="from"/> in a straight run: the most
        /// used same-turn join between them whose offset is mostly along one horizontal axis; null when there is none.
        /// </summary>
        public KitJoin? StraightStep(int from, int to) =>
            Between(from, to).Where(j => j.Turn == 0 && j.Reach > 1 && Math.Abs(j.Offset.Z) < 0.25f * j.Reach)
                             // a run going either way is the same run: of equally common joins, the one forward (+x, then +y)
                             .OrderByDescending(j => j.Count).ThenByDescending(j => j.Offset.X).ThenByDescending(j => j.Offset.Y)
                             .Cast<KitJoin?>().FirstOrDefault();
    }
}
