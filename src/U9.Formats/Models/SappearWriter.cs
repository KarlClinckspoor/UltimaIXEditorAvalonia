using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace U9.Formats.Models
{
    /// <summary>
    /// Builds a sappear.flx model record from triangles: one limb (no transform), one LOD.
    /// <para>What is written, as read from the shipped models: header +0x08 the box's centre, +0x14 its height, +0x18
    /// the distance from the centre to the farthest vertex, +0x1C and +0x28 the same centre and radius again, +0x30 / +0x3C the box,
    /// +0x48 the LOD thresholds the shipped models use, +0x58 the centre once more; the limb table at +0x90, the limb
    /// header at 0x98 and the LOD block at 0xC8, as in every one-limb, one-LOD model. The LOD block: +0x04 flags,
    /// +0x0C centre, +0x18 radius, +0x1C box, counts, offsets; then the faces, vertices (shared corners merged),
    /// materials (runs of faces with one texture and frame) and the four face lists. A face stores its plane at +0x5C
    /// (normal) and +0x68 (distance, n·p + d = 0). The lists are draw orders, a 0xFFFF and every face, padded to 4
    /// bytes; the shipped ones run roughly along +x, +y, -x and -y, so these are sorted that way by face centre.</para>
    /// <para>Not understood, and taken from <paramref name="template"/> when given (the model being replaced): the
    /// header's value at +0x64 and the LOD flags. Per-face +0x74 / +0x78 and the materials' +0x02..+0x07 are left 0.</para>
    /// </summary>
    public static class SappearWriter
    {
        public const int FaceSize = 0x7C, MaterialSize = 0x18;
        static readonly uint[] Thresholds = { 0x3D090, 0x225510, 0x3D0900, 0xFFFFFFFF };

        public static byte[] Build(IReadOnlyList<ModelTriangle> tris, byte[] template = null)
        {
            if (tris == null || tris.Count == 0) throw new ArgumentException("A model needs at least one triangle", nameof(tris));
            if (tris.Count > 0xFFFF) throw new ArgumentException("Too many triangles for a model (65535 at most)", nameof(tris));
            bool haveTemplate = SappearModel.IsModel(template);

            // faces sorted into material runs; corners merged into shared vertices by position
            var faces = tris.Select((t, i) => (t, i)).OrderBy(x => x.t.Texture).ThenBy(x => x.t.Frame).ThenBy(x => x.i).Select(x => x.t).ToList();
            var verts = new List<Vector3>();
            var vertexOf = new Dictionary<Vector3, int>();
            int Vertex(Vector3 p)
            {
                if (!vertexOf.TryGetValue(p, out int k)) { k = verts.Count; verts.Add(p); vertexOf[p] = k; }
                return k;
            }
            var corners = faces.Select(f => (Vertex(f.P0), Vertex(f.P1), Vertex(f.P2))).ToList();
            var runs = new List<(int Texture, int Frame, int First, int Count)>();
            for (int i = 0; i < faces.Count; i++)
            {
                if (runs.Count > 0 && runs[^1].Texture == faces[i].Texture && runs[^1].Frame == faces[i].Frame) { var r = runs[^1]; runs[^1] = (r.Texture, r.Frame, r.First, r.Count + 1); }
                else runs.Add((faces[i].Texture, faces[i].Frame, i, 1));
            }

            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var v in verts) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            // the game's tool started its maximum at FLT_MIN (the smallest positive float), not at -FLT_MAX, so no maximum
            // in the shipped headers is below ~0 (3,008 of 3,112 one-limb models match that way, 2,986 without it)
            max = Vector3.Max(max, new Vector3(1.17549435E-38f));
            var centre = (min + max) / 2;
            float radius = verts.Max(v => Vector3.Distance(centre, v)), height = max.Z - min.Z;

            int faceBytes = faces.Count * FaceSize, vertBytes = verts.Count * 12, matBytes = runs.Count * MaterialSize;
            int listBytes = ((faces.Count + 1) * 2 + 3) & ~3;
            const int lodAt = 0xC8, lodHeader = 0x80;
            int lodSize = lodHeader + faceBytes + vertBytes + matBytes + 4 * listBytes;
            var r8 = new byte[lodAt + lodSize + 4];

            void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(r8.AsSpan(at), v);
            void U(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(r8.AsSpan(at), v);
            void V(int at, Vector3 v) { F(at, v.X); F(at + 4, v.Y); F(at + 8, v.Z); }

            // header
            U(0, 1); U(4, 1);
            V(0x08, centre); F(0x14, height); F(0x18, radius); V(0x1C, centre); F(0x28, radius);
            V(0x30, min); V(0x3C, max);
            for (int i = 0; i < 4; i++) U(0x48 + 4 * i, Thresholds[i]);
            V(0x58, centre);
            if (haveTemplate) U(0x64, BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(0x64)));
            U(0x90, 0x98); U(0x94, lodAt);
            // limb: id 0, parent 0 (root), scale 1, no move, quaternion (w, x, y, z) = (1, 0, 0, 0)
            V(0x98 + 8, Vector3.One); F(0x98 + 0x20, 1);

            // LOD block
            int L = lodAt, B = L + 4;
            U(L, (uint)lodSize);
            uint flags = 4;
            if (haveTemplate)
            {
                int tl = BinaryPrimitives.ReadInt32LittleEndian(template.AsSpan(0x90 + 4));
                if (tl > 0 && tl + 8 <= template.Length) flags = BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(tl + 4));
            }
            U(L + 4, flags);
            V(L + 0x0C, centre); F(L + 0x18, radius); V(L + 0x1C, min); V(L + 0x28, max);
            int fo = 0x7C, vo = fo + faceBytes, mo = vo + vertBytes, lo = mo + matBytes;
            uint[] counts = { (uint)faces.Count, 0, (uint)verts.Count, 0, (uint)faces.Count, (uint)runs.Count };
            for (int i = 0; i < 6; i++) U(L + 0x3C + 4 * i, counts[i]);
            int[] offs = { fo, vo, vo, mo, mo, lo, lo + listBytes, lo + 2 * listBytes, lo + 3 * listBytes };
            for (int i = 0; i < 9; i++) U(L + 0x54 + 4 * i, (uint)offs[i]);

            for (int i = 0; i < faces.Count; i++)
            {
                var t = faces[i];
                int f = B + fo + FaceSize * i;
                var (a, b, c) = corners[i];
                var ns = new[] { t.N0, t.N1, t.N2 };
                var uvs = new[] { t.T0, t.T1, t.T2 };
                int[] idx = { a, b, c };
                for (int k = 0; k < 3; k++)
                {
                    int cn = f + 0x1C * k;
                    U(cn, (uint)idx[k]);
                    V(cn + 8, ns[k]);
                    F(cn + 0x14, uvs[k].X); F(cn + 0x18, uvs[k].Y);
                }
                U(f + 0x54, t.Flags);
                var n = Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
                // the plane faces the way the corner normals do
                if (Vector3.Dot(n, t.N0 + t.N1 + t.N2) < 0) n = -n;
                V(f + 0x5C, n);
                F(f + 0x68, -Vector3.Dot(n, t.P0));
                U(f + 0x70, t.Colour);
            }
            for (int i = 0; i < verts.Count; i++) V(B + vo + 12 * i, verts[i]);
            for (int i = 0; i < runs.Count; i++)
            {
                int m = B + mo + MaterialSize * i;
                var (tex, frame, first, count) = runs[i];
                BinaryPrimitives.WriteUInt16LittleEndian(r8.AsSpan(m), (ushort)(tex < 0 ? 0xFFFF : tex));
                BinaryPrimitives.WriteUInt16LittleEndian(r8.AsSpan(m + 8), (ushort)first);
                BinaryPrimitives.WriteUInt16LittleEndian(r8.AsSpan(m + 0xA), (ushort)count);
                BinaryPrimitives.WriteUInt16LittleEndian(r8.AsSpan(m + 0xC), 0xFFFF);
                r8[m + 0xE] = (byte)frame; r8[m + 0xF] = (byte)frame;
            }
            var centres = faces.Select(t => (t.P0 + t.P1 + t.P2) / 3).ToArray();
            Vector3[] dirs = { Vector3.UnitX, Vector3.UnitY, -Vector3.UnitX, -Vector3.UnitY };
            for (int k = 0; k < 4; k++)
            {
                int at = B + lo + listBytes * k;
                BinaryPrimitives.WriteUInt16LittleEndian(r8.AsSpan(at), 0xFFFF);
                var order = Enumerable.Range(0, faces.Count).OrderBy(i => Vector3.Dot(centres[i], dirs[k])).ThenBy(i => i).ToArray();
                for (int i = 0; i < order.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(r8.AsSpan(at + 2 + 2 * i), (ushort)order[i]);
            }
            return r8;
        }
    }
}
