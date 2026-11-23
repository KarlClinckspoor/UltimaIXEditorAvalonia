using System;
using U9.Formats.Terrain;

namespace U9Editor.Core
{
    /// <summary>
    /// A map's height points as one flat array, row by row (index x + row * Width), resolved through the tile
    /// table. The renderer uploads it as an integer texture and the picker reads heights from it, so neither
    /// needs to know about chunks. Edits go to the map and are copied here with <see cref="Refresh"/>.
    /// </summary>
    public sealed class TerrainRaster
    {
        public int Width { get; }
        public int Height { get; }
        public readonly uint[] Points;
        /// <summary>Highest point height on the map (for framing and picking bounds).</summary>
        public int MaxHeight { get; private set; }

        public TerrainRaster(TerrainMap map)
        {
            Width = map.Width;
            Height = map.Height;
            Points = new uint[Width * Height];
            Refresh(map, 0, 0, Width, Height);
        }

        /// <summary>Copies a rectangle of points (x, row, width, height) from the map.</summary>
        public void Refresh(TerrainMap map, int x0, int y0, int w, int h)
        {
            int x1 = Math.Min(Width, x0 + w), y1 = Math.Min(Height, y0 + h);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            int max = MaxHeight;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    uint p = map.GetPoint(x, y).Raw;
                    Points[x + y * Width] = p;
                    max = Math.Max(max, (int)(p & 0xFFF));
                }
            MaxHeight = max;
        }

        public TerrainPoint this[int x, int row] => new TerrainPoint(Points[x + row * Width]);

        /// <summary>Point height at a clamped position.</summary>
        public int HeightAt(int x, int row)
        {
            x = Math.Clamp(x, 0, Width - 1);
            row = Math.Clamp(row, 0, Height - 1);
            return (int)(Points[x + row * Width] & 0xFFF);
        }

        /// <summary>Bilinear height (in point height units) between points.</summary>
        public float HeightAt(float x, float row)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(row);
            float fx = x - ix, fy = row - iy;
            float a = HeightAt(ix, iy), b = HeightAt(ix + 1, iy), c = HeightAt(ix, iy + 1), d = HeightAt(ix + 1, iy + 1);
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        }
    }
}
