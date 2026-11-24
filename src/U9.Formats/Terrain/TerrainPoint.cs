using System;

namespace U9.Formats.Terrain
{
    /// <summary>
    /// One terrain height point, packed in a u32:
    /// <list type="bullet">
    /// <item>bits 0-11: height, 0-4095, in steps of 0.25 world units.</item>
    /// <item>bit 12: hole. The quad is dropped at full detail and does not block movement.</item>
    /// <item>bits 13-14: rotation of the quad's texture in quarter turns (see <see cref="Rotation"/>).</item>
    /// <item>bit 15: unknown; it does not orient the texture.</item>
    /// <item>bits 16-21: frame within the texture.</item>
    /// <item>bits 22-31: texture number.</item>
    /// </list>
    /// <para>The rotation was found from the 9-frame transition textures (frames 0-2 an outer corner, 3-5 an inner
    /// corner, 6-8 an edge, each showing the second material at known corners): over Britannia's 80,498 pairs of
    /// neighbouring transition quads, the corners they share agree in 98.2% of pairs under this reading (40% under
    /// the swap/mirror reading it replaces). Bits 13-15 are spread evenly over all points, plain ground included,
    /// so plain ground is turned at random to hide the tiling.</para>
    /// </summary>
    public readonly struct TerrainPoint : IEquatable<TerrainPoint>
    {
        public const int MaxHeight = 0xFFF;
        public const float HeightUnit = 0.25f;

        public readonly uint Raw;

        public TerrainPoint(uint raw) { Raw = raw; }

        public int Height => (int)(Raw & 0xFFF);
        public bool Hole => (Raw & (1u << 12)) != 0;
        /// <summary>
        /// Quarter turns of the texture on the quad east and north of the point. With (u, v) the position in the
        /// quad (u east, v north) and the texture's top row at v = 0 of its coordinates: 0 shows (u, 1 - v), 1 shows
        /// (1 - v, 1 - u), 2 shows (1 - u, v), 3 shows (v, u). So 0 puts the texture's top to the north, 2 turns it
        /// half round, and 1 and 3 are the quarter turns.
        /// </summary>
        public int Rotation => (int)((Raw >> 13) & 3);
        public bool Bit15 => (Raw & (1u << 15)) != 0;
        public int Frame => (int)((Raw >> 16) & 0x3F);
        public int Texture => (int)(Raw >> 22);

        /// <summary>Height in world units.</summary>
        public float WorldHeight => Height * HeightUnit;

        public TerrainPoint WithHeight(int height)
        {
            if ((uint)height > MaxHeight) throw new ArgumentOutOfRangeException(nameof(height));
            return new TerrainPoint((Raw & ~0xFFFu) | (uint)height);
        }
        public TerrainPoint WithHole(bool on) => WithBit(12, on);
        public TerrainPoint WithRotation(int quarterTurns) => new TerrainPoint((Raw & ~(3u << 13)) | ((uint)(quarterTurns & 3) << 13));
        public TerrainPoint WithFrame(int frame)
        {
            if ((uint)frame > 0x3F) throw new ArgumentOutOfRangeException(nameof(frame));
            return new TerrainPoint((Raw & ~(0x3Fu << 16)) | ((uint)frame << 16));
        }
        public TerrainPoint WithTexture(int texture)
        {
            if ((uint)texture > 0x3FF) throw new ArgumentOutOfRangeException(nameof(texture));
            return new TerrainPoint((Raw & 0x003FFFFFu) | ((uint)texture << 22));
        }

        TerrainPoint WithBit(int bit, bool on) => new TerrainPoint(on ? Raw | (1u << bit) : Raw & ~(1u << bit));

        public bool Equals(TerrainPoint other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is TerrainPoint p && Equals(p);
        public override int GetHashCode() => (int)Raw;
        public static bool operator ==(TerrainPoint a, TerrainPoint b) => a.Raw == b.Raw;
        public static bool operator !=(TerrainPoint a, TerrainPoint b) => a.Raw != b.Raw;
        public override string ToString() =>
            $"h={Height} tex={Texture} frame={Frame}{(Hole ? " hole" : "")} rot={Rotation}{(Bit15 ? " b15" : "")}";
    }
}
