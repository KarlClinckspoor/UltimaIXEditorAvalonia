using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using U9.Formats.Bitmaps;
using U9.Formats.Models;

namespace U9Editor.Core
{
    /// <summary>
    /// sappear models as glTF 2.0 binary (.glb), for Blender and other tools, and back.
    /// <para>Axes and units: glTF is y up; a model's (x east, y north, z up) goes to glTF (x, z, -y), which Blender's
    /// importer turns back into x east, y north, z up. One glTF unit is 128 object units (one terrain point).</para>
    /// <para>Each material is one primitive. Its extras keep what the game needs to rebuild the faces:
    /// <c>u9_texture</c> (bitmap16.flx record, -1 for a coloured face), <c>u9_frame</c>, <c>u9_flags</c> (face flags;
    /// bit 3 = not drawn, the blocking walls) and <c>u9_colour</c> (RGBA as stored). Textures are embedded as PNG.</para>
    /// </summary>
    public static class GltfModel
    {
        public const float UnitsPerMetre = 128f;

        static Vector3 ToGltf(Vector3 v) => new Vector3(v.X, v.Z, -v.Y);
        static Vector3 FromGltf(Vector3 v) => new Vector3(v.X, -v.Z, v.Y);

        /// <summary>The model at one LOD as a .glb file. <paramref name="readBitmap"/> gives bitmap16.flx records (null: untextured).</summary>
        public static byte[] Export(byte[] record, Func<int, byte[]> readBitmap, int lod = 0, string name = "model")
        {
            var tris = SappearModel.Triangles(record, lod);
            return Export(tris, readBitmap, name);
        }

        public static byte[] Export(IReadOnlyList<ModelTriangle> tris, Func<int, byte[]> readBitmap, string name = "model")
        {
            var bin = new MemoryStream();
            var bufferViews = new JsonArray();
            var accessors = new JsonArray();
            var images = new JsonArray();
            var textures = new JsonArray();
            var materials = new JsonArray();
            var primitives = new JsonArray();
            var imageOf = new Dictionary<(int, int), int>();

            int View(byte[] data, int? target)
            {
                while (bin.Length % 4 != 0) bin.WriteByte(0);
                var v = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
                if (target is int t) v["target"] = t;
                bin.Write(data);
                bufferViews.Add(v);
                return bufferViews.Count - 1;
            }

            int Accessor(float[] data, int components, string type, bool minMax)
            {
                var bytes = new byte[data.Length * 4];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                var a = new JsonObject
                {
                    ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = data.Length / components, ["type"] = type,
                };
                if (minMax)
                {
                    var min = new JsonArray(); var max = new JsonArray();
                    for (int c = 0; c < components; c++)
                    {
                        float lo = float.MaxValue, hi = float.MinValue;
                        for (int i = c; i < data.Length; i += components) { lo = Math.Min(lo, data[i]); hi = Math.Max(hi, data[i]); }
                        min.Add(lo); max.Add(hi);
                    }
                    a["min"] = min; a["max"] = max;
                }
                accessors.Add(a);
                return accessors.Count - 1;
            }

            int ImageFor(int texture, int frame, out bool transparent)
            {
                transparent = false;
                var rec = texture >= 0 ? readBitmap?.Invoke(texture) : null;
                if (rec == null || Bitmap16.FrameCount(rec) == 0) return -1;
                var f = Bitmap16.ReadFrame(rec, Math.Min(frame, Bitmap16.FrameCount(rec) - 1));
                transparent = f.HasTransparency;
                if (imageOf.TryGetValue((texture, frame), out int img)) return img;
                var png = PngWriter.EncodeRgba(f.Width, f.Height, f.Rgba);
                images.Add(new JsonObject { ["bufferView"] = View(png, null), ["mimeType"] = "image/png", ["name"] = $"bitmap16_{texture}_{frame}" });
                textures.Add(new JsonObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                img = textures.Count - 1;
                imageOf[(texture, frame)] = img;
                return img;
            }

            // one primitive per material: texture, frame, flags and (untextured) colour
            foreach (var g in tris.GroupBy(t => (t.Texture, t.Frame, t.Flags, Colour: t.Texture < 0 ? t.Colour : 0u)).OrderBy(g => g.Key.Texture).ThenBy(g => g.Key.Frame))
            {
                var list = g.ToList();
                var pos = new float[list.Count * 9];
                var nrm = new float[list.Count * 9];
                var uv = new float[list.Count * 6];
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    var ps = new[] { t.P0, t.P1, t.P2 };
                    var ns = new[] { t.N0, t.N1, t.N2 };
                    var ts = new[] { t.T0, t.T1, t.T2 };
                    for (int k = 0; k < 3; k++)
                    {
                        var p = ToGltf(ps[k]) / UnitsPerMetre;
                        var n = ToGltf(ns[k]);
                        int o = (i * 3 + k) * 3;
                        pos[o] = p.X; pos[o + 1] = p.Y; pos[o + 2] = p.Z;
                        nrm[o] = n.X; nrm[o + 1] = n.Y; nrm[o + 2] = n.Z;
                        uv[(i * 3 + k) * 2] = ts[k].X; uv[(i * 3 + k) * 2 + 1] = ts[k].Y;
                    }
                }
                var (texture, frame, flags, colour) = g.Key;
                int tex = ImageFor(texture, frame, out bool transparent);
                var pbr = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 };
                if (tex >= 0) pbr["baseColorTexture"] = new JsonObject { ["index"] = tex };
                else
                {
                    uint c = list[0].Colour;
                    pbr["baseColorFactor"] = new JsonArray((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
                }
                var mat = new JsonObject
                {
                    ["name"] = (flags & 8) != 0 ? $"u9_invisible_{materials.Count}" : texture >= 0 ? $"u9_tex{texture}_f{frame}" : $"u9_colour_{list[0].Colour:X8}",
                    ["pbrMetallicRoughness"] = pbr,
                    ["doubleSided"] = true,
                    ["extras"] = new JsonObject { ["u9_texture"] = texture, ["u9_frame"] = frame, ["u9_flags"] = flags, ["u9_colour"] = list[0].Colour },
                };
                if (transparent) { mat["alphaMode"] = "MASK"; mat["alphaCutoff"] = 0.5; }
                materials.Add(mat);
                primitives.Add(new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = Accessor(pos, 3, "VEC3", true), ["NORMAL"] = Accessor(nrm, 3, "VEC3", false), ["TEXCOORD_0"] = Accessor(uv, 2, "VEC2", false),
                    },
                    ["material"] = materials.Count - 1,
                    ["mode"] = 4,
                });
            }
            while (bin.Length % 4 != 0) bin.WriteByte(0);

            var root = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "U9 Editor" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
                ["nodes"] = new JsonArray(new JsonObject { ["name"] = name, ["mesh"] = 0 }),
                ["meshes"] = new JsonArray(new JsonObject { ["name"] = name, ["primitives"] = primitives }),
                ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors,
                ["materials"] = materials,
            };
            if (images.Count > 0)
            {
                root["images"] = images;
                root["textures"] = textures;
                root["samplers"] = new JsonArray(new JsonObject { ["wrapS"] = 10497, ["wrapT"] = 10497 });
            }
            return Glb(Encoding.UTF8.GetBytes(root.ToJsonString()), bin.ToArray());
        }

        static byte[] Glb(byte[] json, byte[] bin)
        {
            int jsonPad = (4 - json.Length % 4) % 4, binPad = (4 - bin.Length % 4) % 4;
            int total = 12 + 8 + json.Length + jsonPad + (bin.Length > 0 ? 8 + bin.Length + binPad : 0);
            var o = new MemoryStream(total);
            var w = new BinaryWriter(o);
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
            w.Write((uint)(json.Length + jsonPad)); w.Write(0x4E4F534Au); w.Write(json); for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
            if (bin.Length > 0) { w.Write((uint)(bin.Length + binPad)); w.Write(0x004E4942u); w.Write(bin); for (int i = 0; i < binPad; i++) w.Write((byte)0); }
            return o.ToArray();
        }

        /// <summary>
        /// The triangles of a .glb (every mesh of the default scene's nodes, with node transforms), in model space. A
        /// material's u9 extras give each face its texture, frame, flags and colour; without them a face is untextured
        /// and takes the material's base colour.
        /// </summary>
        public static List<ModelTriangle> Import(byte[] glb)
        {
            var (json, bin) = ReadGlb(glb);
            var root = JsonNode.Parse(json).AsObject();
            var tris = new List<ModelTriangle>();
            var nodes = root["nodes"]?.AsArray();
            var meshes = root["meshes"]?.AsArray();
            if (nodes == null || meshes == null) return tris;
            int sceneIndex = root["scene"]?.GetValue<int>() ?? 0;
            var sceneNodes = root["scenes"]?[sceneIndex]?["nodes"]?.AsArray().Select(n => n.GetValue<int>()).ToList()
                             ?? Enumerable.Range(0, nodes.Count).ToList();

            void Walk(int ni, Matrix4x4 parent, int depth)
            {
                if (depth > 64) return;
                var node = nodes[ni].AsObject();
                var m = LocalMatrix(node) * parent;
                if (node["mesh"] is JsonNode mi) AddMesh(meshes[mi.GetValue<int>()].AsObject(), m);
                if (node["children"] is JsonArray ch) foreach (var c in ch) Walk(c.GetValue<int>(), m, depth + 1);
            }

            void AddMesh(JsonObject mesh, Matrix4x4 m)
            {
                Matrix4x4.Invert(m, out var inv);
                var nm = Matrix4x4.Transpose(inv);
                foreach (var prim in mesh["primitives"].AsArray())
                {
                    int mode = prim["mode"]?.GetValue<int>() ?? 4;
                    if (mode != 4) continue;
                    var attr = prim["attributes"].AsObject();
                    var pos = Floats(root, bin, attr["POSITION"].GetValue<int>(), 3);
                    var nrm = attr["NORMAL"] is JsonNode n ? Floats(root, bin, n.GetValue<int>(), 3) : null;
                    var uv = attr["TEXCOORD_0"] is JsonNode u ? Floats(root, bin, u.GetValue<int>(), 2) : null;
                    int count = pos.Length / 3;
                    var idx = prim["indices"] is JsonNode ii ? Indices(root, bin, ii.GetValue<int>()) : Enumerable.Range(0, count).ToArray();
                    int texture = -1, frame = 0; uint flags = 0, colour = 0xFFFFFFFF;
                    if (prim["material"] is JsonNode matIndex && root["materials"]?[matIndex.GetValue<int>()] is JsonObject mat)
                    {
                        var ex = mat["extras"] as JsonObject;
                        if (Number(ex?["u9_texture"]) is long t) texture = (int)t;
                        if (Number(ex?["u9_frame"]) is long f) frame = (int)f;
                        if (Number(ex?["u9_flags"]) is long fl) flags = (uint)fl;
                        if (Number(ex?["u9_colour"]) is long co) colour = (uint)co;
                        else if (mat["pbrMetallicRoughness"]?["baseColorFactor"] is JsonArray bc && bc.Count >= 3)
                            colour = (uint)Math.Clamp(bc[0].GetValue<float>() * 255, 0, 255) | (uint)Math.Clamp(bc[1].GetValue<float>() * 255, 0, 255) << 8 |
                                     (uint)Math.Clamp(bc[2].GetValue<float>() * 255, 0, 255) << 16 | 0xFF000000u;
                    }
                    Vector3 P(int i) => FromGltf(Vector3.Transform(new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]), m)) * UnitsPerMetre;
                    Vector3 N(int i) => nrm == null ? Vector3.Zero : Vector3.Normalize(FromGltf(Vector3.TransformNormal(new Vector3(nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]), nm)));
                    Vector2 T(int i) => uv == null ? Vector2.Zero : new Vector2(uv[2 * i], uv[2 * i + 1]);
                    for (int k = 0; k + 2 < idx.Length; k += 3)
                    {
                        int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                        var t = new ModelTriangle
                        {
                            P0 = P(a), P1 = P(b), P2 = P(c), N0 = N(a), N1 = N(b), N2 = N(c), T0 = T(a), T1 = T(b), T2 = T(c),
                            Texture = texture, Frame = frame, Flags = flags, Colour = colour,
                        };
                        if (nrm == null)
                        {
                            var fn = Vector3.Normalize(Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0));
                            if (float.IsNaN(fn.X)) fn = Vector3.UnitZ;
                            t.N0 = t.N1 = t.N2 = fn;
                        }
                        tris.Add(t);
                    }
                }
            }

            foreach (int ni in sceneNodes) Walk(ni, Matrix4x4.Identity, 0);
            return tris;
        }

        /// <summary>
        /// A number from an extras value: Blender keeps custom properties as 32-bit signed integers, so it writes larger
        /// ones (a colour) as strings, and negative ones for values past 2^31.
        /// </summary>
        static long? Number(JsonNode n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue(out long l)) return l < 0 && l >= int.MinValue ? (uint)(int)l : l;
            if (v.TryGetValue(out double d)) return (long)d;
            if (v.TryGetValue(out string s))
            {
                s = s.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out long h)) return h;
                if (long.TryParse(s, out long p)) return p;
            }
            return null;
        }

        static Matrix4x4 LocalMatrix(JsonObject node)
        {
            if (node["matrix"] is JsonArray ma && ma.Count == 16)
            {
                var f = ma.Select(x => x.GetValue<float>()).ToArray();
                // glTF is column-major with column vectors; System.Numerics uses row vectors: the same 16 numbers in order
                return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            }
            var s = node["scale"] is JsonArray sa ? new Vector3(sa[0].GetValue<float>(), sa[1].GetValue<float>(), sa[2].GetValue<float>()) : Vector3.One;
            var r = node["rotation"] is JsonArray ra ? new Quaternion(ra[0].GetValue<float>(), ra[1].GetValue<float>(), ra[2].GetValue<float>(), ra[3].GetValue<float>()) : Quaternion.Identity;
            var t = node["translation"] is JsonArray ta ? new Vector3(ta[0].GetValue<float>(), ta[1].GetValue<float>(), ta[2].GetValue<float>()) : Vector3.Zero;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }

        static (string Json, byte[] Bin) ReadGlb(byte[] glb)
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67) throw new InvalidDataException("Not a binary glTF (.glb) file");
            int at = 12;
            string json = null; byte[] bin = Array.Empty<byte>();
            while (at + 8 <= glb.Length)
            {
                int len = BitConverter.ToInt32(glb, at);
                uint type = BitConverter.ToUInt32(glb, at + 4);
                if (len < 0 || at + 8 + len > glb.Length) throw new InvalidDataException("A .glb chunk runs past the end");
                if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(glb, at + 8, len);
                else if (type == 0x004E4942) bin = glb.AsSpan(at + 8, len).ToArray();
                at += 8 + len;
            }
            if (json == null) throw new InvalidDataException("The .glb has no JSON chunk");
            return (json, bin);
        }

        static (byte[] Data, int Offset, int Stride, int Count, int ComponentType) View(JsonObject root, byte[] bin, int accessor)
        {
            var a = root["accessors"][accessor].AsObject();
            if (a["bufferView"] == null) throw new InvalidDataException("Sparse or empty accessors are not supported");
            var v = root["bufferViews"][a["bufferView"].GetValue<int>()].AsObject();
            if ((v["buffer"]?.GetValue<int>() ?? 0) != 0) throw new InvalidDataException("Only the .glb's own buffer is supported");
            int offset = (v["byteOffset"]?.GetValue<int>() ?? 0) + (a["byteOffset"]?.GetValue<int>() ?? 0);
            return (bin, offset, v["byteStride"]?.GetValue<int>() ?? 0, a["count"].GetValue<int>(), a["componentType"].GetValue<int>());
        }

        static float[] Floats(JsonObject root, byte[] bin, int accessor, int components)
        {
            var (data, offset, stride, count, type) = View(root, bin, accessor);
            if (type != 5126) throw new InvalidDataException("Only float positions, normals and UVs are supported");
            if (stride == 0) stride = 4 * components;
            var f = new float[count * components];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < components; c++) f[i * components + c] = BitConverter.ToSingle(data, offset + i * stride + 4 * c);
            return f;
        }

        static int[] Indices(JsonObject root, byte[] bin, int accessor)
        {
            var (data, offset, stride, count, type) = View(root, bin, accessor);
            int size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("Unknown index type") };
            if (stride == 0) stride = size;
            var r = new int[count];
            for (int i = 0; i < count; i++)
                r[i] = size == 1 ? data[offset + i * stride] : size == 2 ? BitConverter.ToUInt16(data, offset + i * stride) : (int)BitConverter.ToUInt32(data, offset + i * stride);
            return r;
        }
    }
}
