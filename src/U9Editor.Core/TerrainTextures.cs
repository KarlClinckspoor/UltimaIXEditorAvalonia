using System;
using System.Collections.Generic;
using U9.Formats.Bitmaps;
using U9.Formats.Terrain;

namespace U9Editor.Core
{
    /// <summary>
    /// The terrain textures one map uses, ready for a texture array: one 128 x 128 RGBA layer per
    /// (texture, frame) pair, and a lookup from (frame, texture) to layer + 1 (0 = not loaded).
    /// A point's texture number is a bitmap16.flx record and its frame a frame of that record.
    /// </summary>
    public sealed class TerrainTextures
    {
        public const int LayerSize = 128;
        public const int Frames = 64, Textures = 1024;

        public readonly List<byte[]> Layers = new List<byte[]>();
        /// <summary>Layer + 1 for index frame + texture * 64; 0 when the pair is missing.</summary>
        public readonly ushort[] Lookup = new ushort[Frames * Textures];
        /// <summary>Pairs the map uses that bitmap16.flx does not have (texture, frame).</summary>
        public readonly List<(int Texture, int Frame)> Missing = new List<(int, int)>();

        /// <param name="readRecord">Reads a bitmap16.flx record, null if the slot is empty.</param>
        public static TerrainTextures Build(TerrainRaster raster, Func<int, byte[]> readRecord)
        {
            var set = new TerrainTextures();
            var pairs = new SortedSet<int>();
            foreach (uint raw in raster.Points) pairs.Add((int)(raw >> 22) * Frames + (int)((raw >> 16) & 63));
            var records = new Dictionary<int, byte[]>();
            foreach (int key in pairs)
            {
                int tex = key / Frames, frame = key % Frames;
                if (!records.TryGetValue(tex, out var rec)) records[tex] = rec = readRecord(tex);
                if (rec == null || frame >= Bitmap16.FrameCount(rec)) { set.Missing.Add((tex, frame)); continue; }
                BitmapFrame f;
                try { f = Bitmap16.ReadFrame(rec, frame); }
                catch (System.IO.InvalidDataException) { set.Missing.Add((tex, frame)); continue; }
                set.Layers.Add(Resample(f, LayerSize));
                set.Lookup[key] = (ushort)set.Layers.Count;
            }
            return set;
        }

        /// <summary>Nearest-neighbour resample to size x size (terrain frames are 128 x 128; a few are 64 x 64).</summary>
        static byte[] Resample(BitmapFrame f, int size)
        {
            if (f.Width == size && f.Height == size) return f.Rgba;
            var o = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    Buffer.BlockCopy(f.Rgba, ((x * f.Width / size) + (y * f.Height / size) * f.Width) * 4, o, (x + y * size) * 4, 4);
            return o;
        }
    }
}
