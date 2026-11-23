using System;
using System.Buffers.Binary;

namespace U9.Formats.Terrain
{
    /// <summary>
    /// 16 x 16 terrain points, stored row by row (index x + y * 16). The row order is the natural reading of the
    /// file; the renderer will show whether it is right.
    /// </summary>
    public sealed class TerrainChunk
    {
        public const int Size = 16;
        public const int PointCount = Size * Size;
        public const int ByteSize = PointCount * 4;

        public readonly uint[] Points = new uint[PointCount];

        public TerrainPoint this[int x, int y]
        {
            get => new TerrainPoint(Points[x + y * Size]);
            set => Points[x + y * Size] = value.Raw;
        }

        public static TerrainChunk Read(ReadOnlySpan<byte> data)
        {
            var c = new TerrainChunk();
            for (int i = 0; i < PointCount; i++) c.Points[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4));
            return c;
        }

        public void Write(Span<byte> data)
        {
            for (int i = 0; i < PointCount; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(i * 4), Points[i]);
        }

        public TerrainChunk Clone()
        {
            var c = new TerrainChunk();
            Array.Copy(Points, c.Points, PointCount);
            return c;
        }
    }
}
