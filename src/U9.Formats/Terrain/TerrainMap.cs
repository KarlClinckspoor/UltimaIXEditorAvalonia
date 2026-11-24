using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace U9.Formats.Terrain
{
    /// <summary>
    /// A terrain file, static/terrain.N.
    /// <para>Layout (all little-endian):</para>
    /// <list type="bullet">
    /// <item>0x00, u32: width in height points (a multiple of 16; Britannia is 1024).</item>
    /// <item>0x04, u32: height in height points.</item>
    /// <item>0x08, 0x80 bytes: map name, NUL-terminated. The bytes after the NUL are leftovers and are kept.</item>
    /// <item>0x88, i32: water level, in quarter height units: Britannia's 2024 is height 506, where its coast is (-12 on the Abyss).</item>
    /// <item>0x8C, f32: wave amplitude.</item>
    /// <item>0x90, u32: flags, meaning unknown.</item>
    /// <item>0x94, u32: number of chunks.</item>
    /// <item>0x98: one u16 chunk number per tile, (width/16) x (height/16) tiles, row by row.</item>
    /// <item>Then the chunks, each 16 x 16 u32 <see cref="TerrainPoint"/>s. Tiles can share a chunk.</item>
    /// </list>
    /// Several shipped files hold more bytes after the last chunk (leftovers from an older, longer version of
    /// the file) and three claim chunks that are not there; both are kept so the file writes back unchanged.
    /// </summary>
    public sealed class TerrainMap
    {
        public const int HeaderSize = 0x98;
        public const int NameSize = 0x80;
        public const int TileSize = TerrainChunk.Size;

        /// <summary>Width in height points.</summary>
        public int Width { get; private set; }
        /// <summary>Height (north-south extent) in height points.</summary>
        public int Height { get; private set; }
        public int TilesX => Width / TileSize;
        public int TilesY => Height / TileSize;

        /// <summary>The raw 128-byte name field.</summary>
        public byte[] NameField { get; set; } = new byte[NameSize];
        /// <summary>Water level as stored, in quarter height units (4 x a point height). See <see cref="WaterHeight"/>.</summary>
        public int WaterLevel { get; set; }
        /// <summary>Water level on the same scale as <see cref="TerrainPoint.Height"/>.</summary>
        public float WaterHeight => WaterLevel / 4f;
        public float WaveAmplitude { get; set; }
        public uint Flags { get; set; }

        /// <summary>Chunk number for each tile, index tileX + tileY * TilesX. Change points through <see cref="SetPoint"/>,
        /// not by writing here: it keeps count of how many tiles share each chunk.</summary>
        public ushort[] TileChunks { get; private set; } = Array.Empty<ushort>();
        public List<TerrainChunk> Chunks { get; } = new List<TerrainChunk>();

        /// <summary>Bytes after the last chunk, kept as they are. Clear it to drop them.</summary>
        public byte[] TrailingData { get; set; } = Array.Empty<byte>();

        /// <summary>The chunk count from the header when it did not match the chunks present; null otherwise.</summary>
        public uint? DeclaredChunkCount { get; set; }
        int loadedChunks;

        public TerrainMap() { }

        /// <summary>A new, flat map of the given size in height points (multiples of 16), all tiles sharing one chunk.</summary>
        public TerrainMap(int width, int height, string name)
        {
            if (width < 0 || height < 0 || width % TileSize != 0 || height % TileSize != 0)
                throw new ArgumentException("Terrain size must be a multiple of 16 points");
            Width = width;
            Height = height;
            Name = name;
            TileChunks = new ushort[TilesX * TilesY];
            Chunks.Add(new TerrainChunk());
        }

        /// <summary>The map name, the text before the first NUL of the name field (Latin-1).</summary>
        public string Name
        {
            get
            {
                int n = Array.IndexOf(NameField, (byte)0);
                return Encoding.Latin1.GetString(NameField, 0, n < 0 ? NameField.Length : n);
            }
            set
            {
                var b = Encoding.Latin1.GetBytes(value ?? "");
                if (b.Length >= NameSize) throw new ArgumentException($"Terrain name is limited to {NameSize - 1} characters");
                var f = new byte[NameSize];
                b.CopyTo(f, 0);
                NameField = f;
            }
        }

        public static TerrainMap Load(string path) => Read(File.ReadAllBytes(path));

        public static TerrainMap Read(byte[] data)
        {
            if (data.Length < HeaderSize) throw new InvalidDataException("File is shorter than a terrain header");
            var s = data.AsSpan();
            var m = new TerrainMap();
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(s);
            uint h = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4));
            if (w % TileSize != 0 || h % TileSize != 0 || w > 0x10000 || h > 0x10000)
                throw new InvalidDataException($"Terrain size {w}x{h} is not a multiple of {TileSize}");
            m.Width = (int)w;
            m.Height = (int)h;
            m.NameField = s.Slice(8, NameSize).ToArray();
            m.WaterLevel = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0x88));
            m.WaveAmplitude = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(0x8C));
            m.Flags = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(0x90));
            uint chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(0x94));

            int tiles = m.TilesX * m.TilesY;
            int pos = HeaderSize;
            if (pos + tiles * 2 > data.Length) throw new InvalidDataException("Terrain tile table runs past the end of the file");
            m.TileChunks = new ushort[tiles];
            for (int i = 0; i < tiles; i++, pos += 2) m.TileChunks[i] = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(pos));

            long present = Math.Min(chunkCount, (data.Length - pos) / TerrainChunk.ByteSize);
            for (int c = 0; c < present; c++, pos += TerrainChunk.ByteSize)
                m.Chunks.Add(TerrainChunk.Read(s.Slice(pos, TerrainChunk.ByteSize)));
            m.loadedChunks = m.Chunks.Count;
            if (present != chunkCount) m.DeclaredChunkCount = chunkCount;

            for (int i = 0; i < tiles; i++)
                if (m.TileChunks[i] >= m.Chunks.Count)
                    throw new InvalidDataException($"Tile {i} uses chunk {m.TileChunks[i]} of {m.Chunks.Count}");

            m.TrailingData = s.Slice(pos).ToArray();
            return m;
        }

        public byte[] ToBytes()
        {
            int tiles = TilesX * TilesY;
            if (TileChunks.Length != tiles) throw new InvalidOperationException("Tile table does not match the map size");
            int size = HeaderSize + tiles * 2 + Chunks.Count * TerrainChunk.ByteSize + TrailingData.Length;
            var data = new byte[size];
            var s = data.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)Width);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), (uint)Height);
            NameField.AsSpan(0, Math.Min(NameSize, NameField.Length)).CopyTo(s.Slice(8));
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(0x88), WaterLevel);
            BinaryPrimitives.WriteSingleLittleEndian(s.Slice(0x8C), WaveAmplitude);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x90), Flags);
            // Keep a mismatched count only while the chunk list is as it was loaded.
            uint count = DeclaredChunkCount.HasValue && Chunks.Count == loadedChunks ? DeclaredChunkCount.Value : (uint)Chunks.Count;
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x94), count);
            int pos = HeaderSize;
            for (int i = 0; i < tiles; i++, pos += 2) BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(pos), TileChunks[i]);
            foreach (var c in Chunks) { c.Write(s.Slice(pos, TerrainChunk.ByteSize)); pos += TerrainChunk.ByteSize; }
            TrailingData.CopyTo(s.Slice(pos));
            return data;
        }

        public void Save(string path) => File.WriteAllBytes(path, ToBytes());

        /// <summary>The chunk used by the tile at (tileX, tileY).</summary>
        public TerrainChunk ChunkAt(int tileX, int tileY) => Chunks[TileChunks[tileX + tileY * TilesX]];

        /// <summary>The height point at (x, y), in points from the map origin.</summary>
        public TerrainPoint GetPoint(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException();
            return ChunkAt(x / TileSize, y / TileSize)[x % TileSize, y % TileSize];
        }

        /// <summary>
        /// Sets the height point at (x, y). If other tiles share the chunk, this tile gets its own copy first,
        /// so the edit stays where it was made.
        /// </summary>
        public void SetPoint(int x, int y, TerrainPoint p)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException();
            int tile = x / TileSize + y / TileSize * TilesX;
            if (ChunkUseCount(TileChunks[tile]) > 1)
            {
                if (Chunks.Count >= ushort.MaxValue) throw new InvalidOperationException("Too many terrain chunks");
                Chunks.Add(Chunks[TileChunks[tile]].Clone());
                useCounts[TileChunks[tile]]--;
                TileChunks[tile] = (ushort)(Chunks.Count - 1);
                if (useCounts.Length > Chunks.Count - 1) useCounts[Chunks.Count - 1] = 1;
                else useCountsOf = null;       // recount next time
            }
            Chunks[TileChunks[tile]][x % TileSize, y % TileSize] = p;
        }

        /// <summary>
        /// Points tiles at an identical chunk where <see cref="SetPoint"/> gave them a copy that has come back to the
        /// same points (an edit taken back), or that matches another, and drops copies no tile uses. Chunks the file
        /// was loaded with keep their numbers. Returns how many chunks were dropped.
        /// </summary>
        public int ShareCopiedChunks()
        {
            if (Chunks.Count <= loadedChunks) return 0;
            var first = new Dictionary<string, int>();
            string Key(TerrainChunk c)
            {
                var b = new byte[TerrainChunk.ByteSize];
                Buffer.BlockCopy(c.Points, 0, b, 0, b.Length);
                return Convert.ToBase64String(b);
            }
            for (int i = 0; i < Chunks.Count; i++) first.TryAdd(Key(Chunks[i]), i);
            for (int t = 0; t < TileChunks.Length; t++)
            {
                int c = TileChunks[t];
                if (c < loadedChunks || c >= Chunks.Count) continue;
                int j = first[Key(Chunks[c])];
                if (j < c) TileChunks[t] = (ushort)j;
            }
            var used = new bool[Chunks.Count];
            foreach (var c in TileChunks) if (c < used.Length) used[c] = true;
            var renumber = new int[Chunks.Count];
            var kept = new List<TerrainChunk>();
            for (int i = 0; i < Chunks.Count; i++)
            {
                if (i >= loadedChunks && !used[i]) { renumber[i] = -1; continue; }
                renumber[i] = kept.Count;
                kept.Add(Chunks[i]);
            }
            int dropped = Chunks.Count - kept.Count;
            if (dropped == 0) return 0;
            for (int t = 0; t < TileChunks.Length; t++) if (TileChunks[t] < renumber.Length) TileChunks[t] = (ushort)renumber[TileChunks[t]];
            Chunks.Clear();
            Chunks.AddRange(kept);
            useCountsOf = null;
            return dropped;
        }

        // tiles using each chunk, counted once per tile table (a brush sets thousands of points a second)
        int[] useCounts;
        ushort[] useCountsOf;

        int ChunkUseCount(int chunk)
        {
            if (useCountsOf != TileChunks || useCounts.Length < Chunks.Count)
            {
                useCounts = new int[Math.Max(Chunks.Count, 1) + 64];
                foreach (var c in TileChunks) if (c < useCounts.Length) useCounts[c]++;
                useCountsOf = TileChunks;
            }
            return chunk < useCounts.Length ? useCounts[chunk] : 0;
        }
    }
}
