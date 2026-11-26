using System;
using System.Numerics;
using U9.Formats.Terrain;

namespace U9Editor.Core
{
    /// <summary>
    /// Converts between a map's height points and the editor's world space.
    /// <para>World space: x east, y up, z north, one unit per point spacing (left-handed, as in the UW editor).</para>
    /// <para>Scale, from the shipped data: fixed objects are placed at 128 units per point horizontally and their
    /// height is 4 x the terrain height under them (median 3.99 over Britannia's 21,853 ground objects), so one
    /// point spacing is 32 height units. The water level is stored in those object units (4 x a height).</para>
    /// <para>Which way is north: the last point row. The game's own map of Britannia (bitmap16.flx record 534) shows
    /// the main continent with Castle British (Lord British's throne is at point 502, 554) at the top and Terfin at the
    /// bottom, and in terrain.9 the main continent lies at the high rows. Row 0 is south and x runs east; flipping
    /// only the rows gives the game's map, not a mirror image of it. <see cref="NorthIsRowZero"/> is kept as a debug toggle.</para>
    /// </summary>
    public sealed class MapSpace
    {
        public const float HeightUnitsPerPoint = 32f;
        public const float ObjectUnitsPerPoint = 128f;

        public int Width { get; }
        public int Height { get; }
        public bool NorthIsRowZero { get; set; }

        public MapSpace(int width, int height) { Width = width; Height = height; }

        public static float WorldY(int height) => height / HeightUnitsPerPoint;
        public static float WorldYFromObject(float objectZ) => objectZ / 4f / HeightUnitsPerPoint;
        /// <summary>Object height (object units) of a world y.</summary>
        public static float ObjectZFromWorld(float worldY) => worldY * 4f * HeightUnitsPerPoint;

        /// <summary>World z of a point row.</summary>
        public float WorldZ(float row) => NorthIsRowZero ? Height - row : row;
        /// <summary>Point row of a world z.</summary>
        public float Row(float worldZ) => NorthIsRowZero ? Height - worldZ : worldZ;

        public Vector3 World(float x, float row, int height) => new Vector3(x, WorldY(height), WorldZ(row));

        /// <summary>Centre of the map at the given height, for framing the camera.</summary>
        public Vector3 Centre(float y) => new Vector3(Width / 2f, y, Height / 2f);
    }
}
