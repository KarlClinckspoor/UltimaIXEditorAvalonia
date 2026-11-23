using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace U9.Formats.Models
{
    /// <summary>One triangle of a model, already in model space.</summary>
    public struct ModelTriangle
    {
        public Vector3 P0, P1, P2;
        public Vector3 N0, N1, N2;
        public Vector2 T0, T1, T2;
        /// <summary>bitmap16.flx record, or -1 for an untextured face drawn in <see cref="Colour"/>.</summary>
        public int Texture;
        /// <summary>Frame of the texture (the material's first animation frame).</summary>
        public int Frame;
        /// <summary>Face colour, RGBA.</summary>
        public uint Colour;
    }

    /// <summary>
    /// A record of static/sappear.flx: a model made of submeshes ("limbs") in a parent chain, each with LODs.
    /// <para>Header: +0x00 u32 submesh count, +0x04 u32 LOD count, +0x30 / +0x3C bounding box (vec3 f32), +0x48
    /// four u32 LOD thresholds (probably squared distances). +0x90: submesh count x (LOD count + 1) u32 offsets from
    /// the record start: per submesh its header, then one block per LOD.</para>
    /// <para>Submesh header (0x30): +0 u32 limb id, +4 u32 parent limb (itself, or 0, for the root), +8 vec3 scale,
    /// +0x14 vec3 position, +0x20 quaternion as (w, x, y, z) floats. A vertex goes to model space by scale, rotate,
    /// translate at its limb, then at each parent in turn.</para>
    /// <para>LOD block at L: +0 u32 size (0 = empty, the block is only these 4 bytes), +0x3C six u32 counts (faces,
    /// mount faces, vertices, mount vertices, max face, materials), +0x54 offsets (faces, mount faces, vertices, mount
    /// vertices, materials, 4 sorted face lists), all relative to L + 4.</para>
    /// <para>Face (0x7C): three corners of 0x1C bytes (+0 u32 vertex index, +4 u32 ignored, +8 vec3 normal, +0x14
    /// vec2 UV with v = 0 at the texture's top row; UVs wrap), +0x54 u32 flags, +0x60 vec3 face normal, +0x70 RGBA
    /// colour. Vertex: vec3 f32. Material (0x18): +0 u16 texture (bitmap16.flx record, 0xFFFF = use the face colour),
    /// +8 u16 first face, +0xA u16 face count, +0xE u8 first animation frame, +0xF u8 last.</para>
    /// <para>Model space: x east, y north, z up, in object units (128 per terrain point); fronts face -y (south).
    /// 16 records begin with u32 169 and are something else; <see cref="IsModel"/> is false for them.</para>
    /// </summary>
    public static class SappearModel
    {
        public static bool IsModel(byte[] r)
        {
            if (r == null || r.Length < 0x90) return false;
            uint nm = BinaryPrimitives.ReadUInt32LittleEndian(r), nl = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4));
            return nm > 0 && nm < 256 && nl > 0 && nl < 16 && 0x90 + 4L * nm * (nl + 1) <= r.Length;
        }

        sealed class Limb
        {
            public int Id, Parent;
            public Vector3 Scale, Position;
            public Quaternion Rotation;
        }

        static float F(byte[] r, int o) => BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(o));
        static int U32(byte[] r, int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(o));
        static int U16(byte[] r, int o) => BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(o));
        static Vector3 V3(byte[] r, int o) => new Vector3(F(r, o), F(r, o + 4), F(r, o + 8));

        /// <summary>The model's triangles at one LOD (0 = most detailed), in model space.</summary>
        public static List<ModelTriangle> Triangles(byte[] r, int lod = 0)
        {
            if (!IsModel(r)) throw new InvalidDataException("Not a sappear model record");
            int nm = U32(r, 0), nl = U32(r, 4);
            lod = Math.Clamp(lod, 0, nl - 1);
            var limbs = new Limb[nm];
            var byId = new Dictionary<int, Limb>();
            for (int m = 0; m < nm; m++)
            {
                int o = U32(r, 0x90 + 4 * (m * (nl + 1)));
                if (o < 0 || o + 0x30 > r.Length) throw new InvalidDataException($"Submesh {m} header lies outside the record");
                var l = new Limb
                {
                    Id = U32(r, o), Parent = U32(r, o + 4), Scale = V3(r, o + 8), Position = V3(r, o + 0x14),
                    Rotation = new Quaternion(F(r, o + 0x24), F(r, o + 0x28), F(r, o + 0x2C), F(r, o + 0x20)),
                };
                limbs[m] = l;
                byId[l.Id] = l;
            }

            Vector3 ToModel(Limb l, Vector3 v, bool isNormal)
            {
                for (int guard = 0; guard < 32; guard++)
                {
                    if (!isNormal) v *= l.Scale;
                    v = Vector3.Transform(v, l.Rotation);
                    if (!isNormal) v += l.Position;
                    if (l.Parent == l.Id || !byId.TryGetValue(l.Parent, out var p)) break;
                    l = p;
                }
                return v;
            }

            var tris = new List<ModelTriangle>();
            for (int m = 0; m < nm; m++)
            {
                int L = U32(r, 0x90 + 4 * (m * (nl + 1) + 1 + lod));
                if (L <= 0 || L + 0x7C > r.Length || U32(r, L) == 0) continue;
                int B = L + 4;
                int faces = U32(r, L + 0x3C), verts = U32(r, L + 0x44), mats = U32(r, L + 0x50);
                int fo = U32(r, L + 0x54), vo = U32(r, L + 0x5C), mo = U32(r, L + 0x64);
                if (faces == 0) continue;     // two shipped blocks have zero counts and leftover offsets
                if (B + vo + 12L * verts > r.Length || B + fo + 0x7CL * faces > r.Length || (mats > 0 && B + mo + 0x18L * mats > r.Length))
                    throw new InvalidDataException($"Submesh {m} geometry lies outside the record");
                var v = new Vector3[verts];
                for (int i = 0; i < verts; i++) v[i] = ToModel(limbs[m], V3(r, B + vo + 12 * i), false);
                var tex = new int[faces];
                var frame = new int[faces];
                Array.Fill(tex, -1);
                for (int i = 0; i < mats; i++)
                {
                    int mm = B + mo + 0x18 * i;
                    int t = U16(r, mm), first = U16(r, mm + 8), count = U16(r, mm + 0xA);
                    for (int f = first; f < first + count && f < faces; f++) { tex[f] = t == 0xFFFF ? -1 : t; frame[f] = r[mm + 0xE]; }
                }
                for (int i = 0; i < faces; i++)
                {
                    int f = B + fo + 0x7C * i;
                    var t = new ModelTriangle { Texture = tex[i], Frame = frame[i], Colour = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(f + 0x70)) };
                    for (int k = 0; k < 3; k++)
                    {
                        int c = f + 0x1C * k;
                        int idx = U32(r, c);
                        if ((uint)idx >= (uint)verts) throw new InvalidDataException($"Face {i} uses vertex {idx} of {verts}");
                        var p = v[idx];
                        var n = Vector3.Normalize(ToModel(limbs[m], V3(r, c + 8), true));
                        if (float.IsNaN(n.X)) n = Vector3.UnitZ;
                        var uv = new Vector2(F(r, c + 0x14), F(r, c + 0x18));
                        if (k == 0) { t.P0 = p; t.N0 = n; t.T0 = uv; }
                        else if (k == 1) { t.P1 = p; t.N1 = n; t.T1 = uv; }
                        else { t.P2 = p; t.N2 = n; t.T2 = uv; }
                    }
                    tris.Add(t);
                }
            }
            return tris;
        }
    }
}
