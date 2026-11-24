using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Objects;
using U9.Formats.Terrain;

namespace U9Editor.Core
{
    public enum TerrainTool { Raise, Lower, Smooth, Flatten, Paint, Hole }

    /// <summary>What a terrain stroke does. Positions and the radius are in terrain points.</summary>
    public sealed class TerrainBrush
    {
        public TerrainTool Tool = TerrainTool.Raise;
        /// <summary>Radius in points; the height tools fade out towards it.</summary>
        public float Radius = 4;
        /// <summary>Raise / Lower: point heights (quarter world units) per dab at the centre. Smooth / Flatten: 0-1 of the way per dab.</summary>
        public float Strength = 8;
        /// <summary>Flatten: the height to flatten to; null takes the height under the first dab.</summary>
        public int? FlattenTo;
        /// <summary>Paint: the texture and frame to lay.</summary>
        public int Texture, Frame;
        /// <summary>Paint: turn each painted quad a random quarter turn, as the game does on plain ground.</summary>
        public bool RandomTurn = true;
        /// <summary>
        /// Paint: lay the texture as a material on the points' corners and give the quads along the edge the map's
        /// transition frames into what was there (see <see cref="TerrainTransitions"/>). Where the map has no
        /// transition between the two, a quad takes the new texture when three of its corners have it.
        /// </summary>
        public bool Blend;
        /// <summary>Hole: true cuts holes, false fills them.</summary>
        public bool CutHole = true;
        /// <summary>Objects standing on the ground (within <see cref="FollowTolerance"/>) go up and down with it.</summary>
        public bool ObjectsFollow = true;
        /// <summary>Object units (4 per point height) an object may be off the ground and still follow it.</summary>
        public int FollowTolerance = 16;
    }

    /// <summary>Points a terrain stroke changed: raw values before and after, by index x + row * width.</summary>
    public sealed class TerrainChange
    {
        public readonly Dictionary<int, (uint Before, uint After)> Points = new Dictionary<int, (uint, uint)>();
        public int X0 = int.MaxValue, Y0 = int.MaxValue, X1 = int.MinValue, Y1 = int.MinValue;
        public bool IsEmpty => Points.Count == 0;
        public void Grow(int x, int y) { X0 = Math.Min(X0, x); Y0 = Math.Min(Y0, y); X1 = Math.Max(X1, x); Y1 = Math.Max(Y1, y); }
    }

    public sealed partial class MapEditor
    {
        /// <summary>The map's terrain, edited in place; null if this editor has none (then terrain tools do nothing).</summary>
        public TerrainMap Terrain { get; set; }
        /// <summary>Where <see cref="Save"/> writes the terrain.</summary>
        public string TerrainPath { get; set; }
        public bool TerrainChanged => terrainVersion != terrainSaved;
        int terrainVersion, terrainSaved;

        /// <summary>The water level changed: the new level as stored (quarter point heights).</summary>
        public event Action<int> WaterEdited;

        /// <summary>Sets the map's water level, in point heights (as the status bar shows them); one undo step.</summary>
        public void SetWaterHeight(float height)
        {
            if (Terrain == null) throw new InvalidOperationException("This map has no terrain to edit.");
            if (InStroke) EndStroke();
            int stored = (int)MathF.Round(height * 4f);
            if (stored == Terrain.WaterLevel) return;
            undo.Add(new Step { Description = "water level", Water = (Terrain.WaterLevel, stored) });
            redo.Clear();
            terrainVersion++;
            SetWater(stored);
        }

        void SetWater(int stored)
        {
            Terrain.WaterLevel = stored;
            WaterEdited?.Invoke(stored);
        }

        /// <summary>Points changed (x, row, width, height): the renderer sends them again.</summary>
        public event Action<int, int, int, int> TerrainEdited;

        TerrainChange stroke;
        TerrainBrush strokeBrush;
        Dictionary<int, float> strokeHeights;
        int strokeFlatten;
        Random turns = new Random(9);
        HashSet<int> strokeCorners;
        TerrainTransitions transitions;

        /// <summary>The map's transition textures, learnt from its terrain the first time they are needed.</summary>
        public TerrainTransitions Transitions => transitions ??= TerrainTransitions.Learn(Raster);

        public bool InStroke => stroke != null;

        public void BeginStroke(TerrainBrush brush)
        {
            if (Terrain == null) throw new InvalidOperationException("This map has no terrain to edit.");
            if (stroke != null) EndStroke();
            stroke = new TerrainChange();
            strokeBrush = brush;
            strokeHeights = new Dictionary<int, float>();
            strokeCorners = new HashSet<int>();
            strokeFlatten = -1;
        }

        /// <summary>One dab of the stroke's brush centred at (x, row), in points (fractions allowed).</summary>
        public void Dab(float x, float row)
        {
            if (stroke == null) return;
            var b = strokeBrush;
            float r = Math.Max(0.5f, b.Radius);
            int x0 = Math.Max(0, (int)Math.Floor(x - r)), x1 = Math.Min(Raster.Width - 1, (int)Math.Ceiling(x + r));
            int y0 = Math.Max(0, (int)Math.Floor(row - r)), y1 = Math.Min(Raster.Height - 1, (int)Math.Ceiling(row + r));
            if (x1 < x0 || y1 < y0) return;
            if (b.Tool == TerrainTool.Paint && b.Blend) { BlendDab(x, row, r, x0, y0, x1, y1); return; }
            if (b.Tool == TerrainTool.Flatten && strokeFlatten < 0)
                strokeFlatten = b.FlattenTo ?? (int)MathF.Round(Raster.HeightAt(x, row));
            // the raster keeps the heights from before this dab until the end of it, so smoothing reads those
            uint[] snapshot = Raster.Points;
            bool any = false;
            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    float d = MathF.Sqrt((px - x) * (px - x) + (py - row) * (py - row));
                    if (d > r) continue;
                    float w = 0.5f * (1 + MathF.Cos(MathF.PI * d / r));
                    int i = px + py * Raster.Width;
                    var p = new TerrainPoint(Raster.Points[i]);
                    TerrainPoint q = p;
                    switch (b.Tool)
                    {
                        case TerrainTool.Raise:
                        case TerrainTool.Lower:
                        case TerrainTool.Smooth:
                        case TerrainTool.Flatten:
                            {
                                if (!strokeHeights.TryGetValue(i, out float h)) h = p.Height;
                                float target = b.Tool switch
                                {
                                    TerrainTool.Raise => h + b.Strength * w,
                                    TerrainTool.Lower => h - b.Strength * w,
                                    TerrainTool.Smooth => h + (Average(snapshot, px, py) - h) * Math.Clamp(b.Strength, 0, 1) * w,
                                    _ => h + (strokeFlatten - h) * Math.Clamp(b.Strength, 0, 1) * w,
                                };
                                target = Math.Clamp(target, 0, TerrainPoint.MaxHeight);
                                strokeHeights[i] = target;
                                q = p.WithHeight((int)MathF.Round(target));
                                break;
                            }
                        case TerrainTool.Paint:
                            if (p.Texture == b.Texture && p.Frame == b.Frame) break;
                            q = p.WithTexture(b.Texture).WithFrame(b.Frame);
                            if (b.RandomTurn) q = q.WithRotation(turns.Next(4));
                            break;
                        case TerrainTool.Hole:
                            q = p.WithHole(b.CutHole);
                            break;
                    }
                    if (q == p) continue;
                    Set(px, py, q);
                    any = true;
                }
            if (any)
            {
                Raster.Refresh(Terrain, x0, y0, x1 - x0 + 1, y1 - y0 + 1);
                TerrainEdited?.Invoke(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            }
        }

        void BlendDab(float x, float row, float r, int x0, int y0, int x1, int y1)
        {
            var tr = Transitions;
            int m = strokeBrush.Texture, w = Raster.Width;
            var quads = new HashSet<int>();
            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    if ((px - x) * (px - x) + (py - row) * (py - row) > r * r) continue;
                    if (!strokeCorners.Add(px + py * w)) continue;
                    for (int qy = py - 1; qy <= py; qy++)
                        for (int qx = px - 1; qx <= px; qx++)
                            if (qx >= 0 && qy >= 0 && qx < w - 1 && qy < Raster.Height - 1) quads.Add(qx + qy * w);
                }
            if (quads.Count == 0) return;
            var corners = new int[4];
            var now = new int[4];
            int ax0 = int.MaxValue, ay0 = int.MaxValue, ax1 = -1, ay1 = -1;
            foreach (int i in quads)
            {
                int qx = i % w, qy = i / w;
                var before = new TerrainPoint(stroke.Points.TryGetValue(i, out var had) ? had.Before : Raster.Points[i]);
                var cur = new TerrainPoint(Raster.Points[i]);
                int painted = 0;
                for (int k = 0; k < 4; k++)
                {
                    int u = k & 1, v = k >> 1;
                    bool mine = strokeCorners.Contains(qx + u + (qy + v) * w);
                    corners[k] = mine ? m : tr.CornerMaterial(before, u, v);
                    now[k] = tr.CornerMaterial(cur, u, v);
                    if (mine) painted++;
                }
                if (now.SequenceEqual(corners)) continue;     // already shows these corners: keep its frame and turn
                int tex, frame, rot;
                if (!tr.TryQuad(corners, turns, out tex, out frame, out rot))
                {
                    if (painted < 3) continue;
                    tr.TryQuad(new[] { m, m, m, m }, turns, out tex, out frame, out rot);
                }
                var q = before.WithTexture(tex).WithFrame(frame).WithRotation(rot);
                if (q == cur) continue;
                Set(qx, qy, q);
                ax0 = Math.Min(ax0, qx); ay0 = Math.Min(ay0, qy); ax1 = Math.Max(ax1, qx); ay1 = Math.Max(ay1, qy);
            }
            if (ax1 < 0) return;
            Raster.Refresh(Terrain, ax0, ay0, ax1 - ax0 + 1, ay1 - ay0 + 1);
            TerrainEdited?.Invoke(ax0, ay0, ax1 - ax0 + 1, ay1 - ay0 + 1);
        }

        static float Average(uint[] pts, int x, int y, int width, int height)
        {
            float sum = 0; int n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ax = x + dx, ay = y + dy;
                    if ((uint)ax >= (uint)width || (uint)ay >= (uint)height) continue;
                    sum += pts[ax + ay * width] & 0xFFF; n++;
                }
            return sum / n;
        }

        float Average(uint[] pts, int x, int y) => Average(pts, x, y, Raster.Width, Raster.Height);

        void Set(int x, int y, TerrainPoint q)
        {
            int i = x + y * Raster.Width;
            uint before = stroke.Points.TryGetValue(i, out var had) ? had.Before : Raster.Points[i];
            stroke.Points[i] = (before, q.Raw);
            stroke.Grow(x, y);
            Terrain.SetPoint(x, y, q);
        }

        /// <summary>Ends the stroke: it becomes one undo step, with the objects that followed the ground.</summary>
        public void EndStroke()
        {
            var s = stroke;
            var b = strokeBrush;
            stroke = null;
            strokeHeights = null;
            if (s == null) return;
            // points put back as they were by the stroke itself are no change
            foreach (var k in s.Points.Where(kv => kv.Value.Before == kv.Value.After).Select(kv => kv.Key).ToList()) s.Points.Remove(k);
            if (s.IsEmpty) return;
            var step = new Step { Description = b.Tool switch { TerrainTool.Paint => "paint", TerrainTool.Hole => b.CutHole ? "cut holes" : "fill holes", _ => b.Tool.ToString().ToLowerInvariant() }, Terrain = s };
            if (b.ObjectsFollow && b.Tool != TerrainTool.Paint) step.Follow = FollowGround(s, b.FollowTolerance);
            undo.Add(step);
            redo.Clear();
            terrainVersion++;
        }

        /// <summary>Moves objects that stood on the ground before the stroke onto the ground after it.</summary>
        List<(ObjectMap, ObjectEdit)> FollowGround(TerrainChange s, int tolerance)
        {
            int w = Raster.Width;
            uint Before(int x, int y)
            {
                x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, Raster.Height - 1);
                int i = x + y * w;
                return s.Points.TryGetValue(i, out var v) ? v.Before : Raster.Points[i];
            }
            float OldHeight(float x, float row)
            {
                int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(row);
                float fx = x - ix, fy = row - iy;
                float a = Before(ix, iy) & 0xFFF, bb = Before(ix + 1, iy) & 0xFFF, c = Before(ix, iy + 1) & 0xFFF, d = Before(ix + 1, iy + 1) & 0xFFF;
                return (a * (1 - fx) + bb * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
            }
            var result = new List<(ObjectMap, ObjectEdit)>();
            foreach (var map in new[] { Fixed, Nonfixed })
            {
                if (map == null) continue;
                var moves = new List<(MapObject O, int Z)>();
                foreach (var o in map.Objects)
                {
                    if (o.Removed) continue;
                    float px = o.MapX / MapSpace.ObjectUnitsPerPoint, py = o.MapY / MapSpace.ObjectUnitsPerPoint;
                    if (px < s.X0 - 1 || px > s.X1 + 1 || py < s.Y0 - 1 || py > s.Y1 + 1) continue;
                    int oldZ = (int)MathF.Round(OldHeight(px, py) * 4f);
                    if (Math.Abs(o.Z - oldZ) > tolerance) continue;
                    int newZ = Math.Clamp(o.Z + GroundZ(o.MapX, o.MapY) - oldZ, 0, 0xFFFF);
                    if (newZ != o.Z) moves.Add((o, newZ));
                }
                if (moves.Count == 0) continue;
                map.BeginEdit("follow the ground");
                foreach (var (o, z) in moves) o.MoveTo(o.MapX, o.MapY, z);
                var e = map.EndEdit();
                if (e == null) continue;
                result.Add((map, e));
                Bump(map, +1);
                Publish(e);
            }
            return result.Count > 0 ? result : null;
        }

        void ApplyTerrain(TerrainChange s, bool after)
        {
            int w = Raster.Width;
            foreach (var kv in s.Points)
                Terrain.SetPoint(kv.Key % w, kv.Key / w, new TerrainPoint(after ? kv.Value.After : kv.Value.Before));
            Raster.Refresh(Terrain, s.X0, s.Y0, s.X1 - s.X0 + 1, s.Y1 - s.Y0 + 1);
            TerrainEdited?.Invoke(s.X0, s.Y0, s.X1 - s.X0 + 1, s.Y1 - s.Y0 + 1);
        }
    }
}
