using System;
using System.Numerics;

namespace U9Editor.Core
{
    /// <summary>What lies under the mouse.</summary>
    public readonly struct TerrainHit
    {
        public static readonly TerrainHit None = default;
        public readonly bool Hit;
        /// <summary>The quad hit: its lower-left point (x, row).</summary>
        public readonly int X, Row;
        /// <summary>The exact world position hit.</summary>
        public readonly Vector3 World;

        public TerrainHit(int x, int row, Vector3 world) { Hit = true; X = x; Row = row; World = world; }
    }

    /// <summary>
    /// Finds where a ray meets the terrain: marches along the ray in steps smaller than a point spacing, then
    /// narrows the crossing down by halving. Holes are not skipped yet (the game drops them only at full detail).
    /// </summary>
    public static class TerrainPicker
    {
        public static TerrainHit Pick(TerrainRaster raster, MapSpace space, Vector3 origin, Vector3 dir)
        {
            if (raster == null || raster.Width == 0) return TerrainHit.None;
            float top = MapSpace.WorldY(raster.MaxHeight) + 1;

            // Clip the ray to the map's box: x 0..W-1, y 0..top, z over the map's rows.
            float zMin = Math.Min(space.WorldZ(0), space.WorldZ(raster.Height - 1));
            float zMax = Math.Max(space.WorldZ(0), space.WorldZ(raster.Height - 1));
            if (!Clip(origin, dir, new Vector3(0, -1, zMin), new Vector3(raster.Width - 1, top, zMax), out float t0, out float t1))
                return TerrainHit.None;

            const float step = 0.25f;
            float prevT = t0;
            bool prevAbove = Above(raster, space, origin + dir * t0);
            if (!prevAbove) return Make(raster, space, origin + dir * t0);
            for (float t = t0 + step; t <= t1 + step; t += step)
            {
                float tc = Math.Min(t, t1);
                bool above = Above(raster, space, origin + dir * tc);
                if (!above)
                {
                    float lo = prevT, hi = tc;
                    for (int i = 0; i < 12; i++)
                    {
                        float mid = (lo + hi) / 2;
                        if (Above(raster, space, origin + dir * mid)) lo = mid; else hi = mid;
                    }
                    return Make(raster, space, origin + dir * hi);
                }
                prevT = tc;
                if (tc >= t1) break;
            }
            return TerrainHit.None;
        }

        static bool Above(TerrainRaster r, MapSpace s, Vector3 p) =>
            p.Y > MapSpace.WorldY(0) + r.HeightAt(p.X, s.Row(p.Z)) / MapSpace.HeightUnitsPerPoint;

        static TerrainHit Make(TerrainRaster r, MapSpace s, Vector3 p)
        {
            int x = Math.Clamp((int)Math.Floor(p.X), 0, r.Width - 1);
            int row = Math.Clamp((int)Math.Floor(s.Row(p.Z)), 0, r.Height - 1);
            return new TerrainHit(x, row, p);
        }

        /// <summary>Slab test: the part of the ray inside an axis-aligned box.</summary>
        static bool Clip(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float t0, out float t1)
        {
            t0 = 0; t1 = float.MaxValue;
            for (int a = 0; a < 3; a++)
            {
                float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
                float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
                float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z;
                float hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
                if (Math.Abs(da) < 1e-9f)
                {
                    if (oa < lo || oa > hi) return false;
                    continue;
                }
                float ta = (lo - oa) / da, tb = (hi - oa) / da;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Math.Max(t0, ta);
                t1 = Math.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return true;
        }
    }
}
