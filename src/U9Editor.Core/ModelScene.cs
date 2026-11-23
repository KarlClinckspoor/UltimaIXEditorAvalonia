using System;
using System.Collections.Generic;
using System.Numerics;
using U9.Formats.Bitmaps;
using U9.Formats.Models;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>One model's slice of <see cref="ModelScene.Vertices"/> and the objects that use it.</summary>
    public sealed class ModelBatch
    {
        public int Model { get; init; }
        public int FirstVertex { get; init; }
        public int VertexCount { get; init; }
        /// <summary>Per object: 16 floats (model-to-world matrix, System.Numerics layout) and the object id.</summary>
        public readonly List<float> Instances = new List<float>();
        public int InstanceCount => Instances.Count / InstanceFloats;
        public const int InstanceFloats = 17;
    }

    /// <summary>
    /// The models of a map's objects, ready to draw instanced: one vertex buffer holding every model the map uses
    /// once, a batch of instances per model, and the texture layers the models need.
    /// <para>Vertex: position (3), normal (3), uv (2), texture layer (1, -1 = untextured), colour RGBA (4) = 13 floats.</para>
    /// <para>Placement: model space is x east, y north, z up, in object units. An object's model goes to object space
    /// by its quaternion and its position (chunk base + x, y, z), then to the editor's world by dividing by 128
    /// (one point spacing) and swapping y and z, since the editor keeps y up.</para>
    /// </summary>
    public sealed class ModelScene
    {
        public const int VertexFloats = 13;
        /// <summary>Model texture layers are kept small: Britannia alone uses 2,740 texture frames.</summary>
        public const int LayerSize = 64;

        public float[] Vertices { get; private set; } = Array.Empty<float>();
        public readonly List<ModelBatch> Batches = new List<ModelBatch>();
        /// <summary>RGBA layers, LayerSize x LayerSize.</summary>
        public readonly List<byte[]> Layers = new List<byte[]>();
        /// <summary>Models that could not be read, with the reason.</summary>
        public readonly Dictionary<int, string> Failed = new Dictionary<int, string>();
        public int Triangles { get; private set; }

        /// <param name="readModel">Reads a sappear.flx record (null if empty).</param>
        /// <param name="readBitmap">Reads a bitmap16.flx record (null if empty).</param>
        public static ModelScene Build(SceneObjects objects, MapSpace space, TypeTable types,
                                       Func<int, byte[]> readModel, Func<int, byte[]> readBitmap)
        {
            var scene = new ModelScene();
            var verts = new List<float>();
            var batches = new Dictionary<int, ModelBatch>();
            var layerOf = new Dictionary<(int, int), int>();
            var bitmaps = new Dictionary<int, byte[]>();

            int Layer(int tex, int frame)
            {
                if (tex < 0) return -1;
                if (layerOf.TryGetValue((tex, frame), out int l)) return l;
                l = -1;
                if (!bitmaps.TryGetValue(tex, out var rec)) bitmaps[tex] = rec = readBitmap(tex);
                if (rec != null && Bitmap16.FrameCount(rec) > 0)
                {
                    try
                    {
                        var f = Bitmap16.ReadFrame(rec, Math.Min(frame, Bitmap16.FrameCount(rec) - 1));
                        scene.Layers.Add(Resample(f, LayerSize));
                        l = scene.Layers.Count - 1;
                    }
                    catch (System.IO.InvalidDataException) { }
                }
                layerOf[(tex, frame)] = l;
                return l;
            }

            ModelBatch Batch(int model)
            {
                if (batches.TryGetValue(model, out var b)) return b;
                b = null;
                try
                {
                    var rec = readModel(model);
                    if (rec == null || !SappearModel.IsModel(rec)) { scene.Failed[model] = "not a model record"; }
                    else
                    {
                        var tris = SappearModel.Triangles(rec, 0);
                        int first = verts.Count / VertexFloats;
                        foreach (var t in tris)
                        {
                            int layer = Layer(t.Texture, t.Frame);
                            float cr = (t.Colour & 0xFF) / 255f, cg = ((t.Colour >> 8) & 0xFF) / 255f, cb = ((t.Colour >> 16) & 0xFF) / 255f;
                            Add(verts, t.P0, t.N0, t.T0, layer, cr, cg, cb);
                            Add(verts, t.P1, t.N1, t.T1, layer, cr, cg, cb);
                            Add(verts, t.P2, t.N2, t.T2, layer, cr, cg, cb);
                        }
                        scene.Triangles += tris.Count;
                        b = new ModelBatch { Model = model, FirstVertex = first, VertexCount = tris.Count * 3 };
                        scene.Batches.Add(b);
                    }
                }
                catch (System.IO.InvalidDataException e) { scene.Failed[model] = e.Message; }
                batches[model] = b;
                return b;
            }

            foreach (var o in objects.All)
            {
                int type = o.Source.Type;
                if (type == TypeTable.PlaceholderType) continue;
                int model = o.Kind == ObjectFileKind.Nonfixed ? o.Source.Model : types[type].DefaultModel;
                if (model <= 0) continue;
                var b = Batch(model);
                if (b == null) continue;
                var m = Placement(o.Source, space);
                b.Instances.Add(m.M11); b.Instances.Add(m.M12); b.Instances.Add(m.M13); b.Instances.Add(m.M14);
                b.Instances.Add(m.M21); b.Instances.Add(m.M22); b.Instances.Add(m.M23); b.Instances.Add(m.M24);
                b.Instances.Add(m.M31); b.Instances.Add(m.M32); b.Instances.Add(m.M33); b.Instances.Add(m.M34);
                b.Instances.Add(m.M41); b.Instances.Add(m.M42); b.Instances.Add(m.M43); b.Instances.Add(m.M44);
                b.Instances.Add(o.Id);
            }
            scene.Batches.RemoveAll(x => x.InstanceCount == 0);
            scene.Vertices = verts.ToArray();
            return scene;
        }

        /// <summary>Model space to editor world for one object (System.Numerics row-vector convention: v * M).</summary>
        public static Matrix4x4 Placement(MapObject o, MapSpace space)
        {
            var q = o.Rotation;
            if (q.LengthSquared() < 1e-6f) q = Quaternion.Identity; else q = Quaternion.Normalize(q);
            var toObject = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(o.ChunkX + o.X, o.ChunkY + o.Y, o.Z);
            const float s = 1f / MapSpace.ObjectUnitsPerPoint;
            var toWorld = new Matrix4x4(
                s, 0, 0, 0,
                0, 0, space.NorthIsRowZero ? -s : s, 0,
                0, s, 0, 0,
                0, 0, space.NorthIsRowZero ? space.Height : 0, 1);
            return toObject * toWorld;
        }

        static void Add(List<float> v, Vector3 p, Vector3 n, Vector2 uv, int layer, float r, float g, float b)
        {
            v.Add(p.X); v.Add(p.Y); v.Add(p.Z);
            v.Add(n.X); v.Add(n.Y); v.Add(n.Z);
            v.Add(uv.X); v.Add(uv.Y);
            v.Add(layer);
            v.Add(r); v.Add(g); v.Add(b); v.Add(1);
        }

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
