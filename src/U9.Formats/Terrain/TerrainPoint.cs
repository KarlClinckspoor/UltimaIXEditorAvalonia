using System;

namespace U9.Formats.Terrain
{
    /// <summary>
    /// One terrain height point, packed in a u32:
    /// <list type="bullet">
    /// <item>bits 0-11: height, 0-4095, in steps of 0.25 world units.</item>
    /// <item>bit 12: hole. The quad is dropped at full detail and does not block movement.</item>
    /// <item>bit 13: swap. Texture coordinates swap X and Y.</item>
    /// <item>bit 14: mirror. Texture coordinates mirror in X and Y.</item>
    /// <item>bit 15: unknown.</item>
    /// <item>bits 16-21: frame within the texture.</item>
    /// <item>bits 22-31: texture number.</item>
    /// </list>
    /// Which flags mean what is from the public format notes; the editor's renderer still has to confirm swap,
    /// mirror and the frame width against the game.
    /// </summary>
    public readonly struct TerrainPoint : IEquatable<TerrainPoint>
    {
        public const int MaxHeight = 0xFFF;
        public const float HeightUnit = 0.25f;

        public readonly uint Raw;

        public TerrainPoint(uint raw) { Raw = raw; }

        public int Height => (int)(Raw & 0xFFF);
        public bool Hole => (Raw & (1u << 12)) != 0;
        public bool Swap => (Raw & (1u << 13)) != 0;
        public bool Mirror => (Raw & (1u << 14)) != 0;
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
        public TerrainPoint WithSwap(bool on) => WithBit(13, on);
        public TerrainPoint WithMirror(bool on) => WithBit(14, on);
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
            $"h={Height} tex={Texture} frame={Frame}{(Hole ? " hole" : "")}{(Swap ? " swap" : "")}{(Mirror ? " mirror" : "")}{(Bit15 ? " b15" : "")}";
    }
}
