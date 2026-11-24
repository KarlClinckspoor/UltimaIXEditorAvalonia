using System;
using System.Collections.Generic;
using System.Numerics;
using U9.Formats.Bitmaps;
using U9.Formats.Models;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>One model's slice of <see cref="ModelScene.Vertices"/>, and where its instances sit in <see cref="ModelScene.Instances"/>.</summary>
    public sealed class ModelBatch
    {
        public int Model { get; init; }
        public int FirstVertex { get; init; }
        public int VertexCount { get; init; }
        /// <summary>Bounding box in model space.</summary>
        public Vector3 BoxMin { get; init; }
        public Vector3 BoxMax { get; init; }
        public int InstanceFirst { get; internal set; }
        public int InstanceCount { get; internal set; }
        /// <summary>Per instance: 16 floats (model-to-world matrix, System.Numerics layout) and the object id.</summary>
        public const int InstanceFloats = 17;
        internal readonly List<SceneObject> Users = new List<SceneObject>();
    }

    /// <summary>
    /// The models of a map's objects, ready to draw instanced: one vertex buffer holding every model the map uses
    /// once, the instances grouped by model, and the texture layers the models need. Models are read once; after an
    /// edit only the instances are rebuilt (<see cref="RebuildInstances"/>), or one object's (<see cref="SetInstance"/>).
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

        float[] vertices = Array.Empty<float>();
        List<float> growing;
        public float[] Vertices => vertices;
        /// <summary>The models that have instances, in vertex order.</summary>
        public readonly List<ModelBatch> Batches = new List<ModelBatch>();
        /// <summary>RGBA layers, LayerSize x LayerSize.</summary>
        public readonly List<byte[]> Layers = new List<byte[]>();
        /// <summary>Models that could not be read, with the reason.</summary>
        public readonly Dictionary<int, string> Failed = new Dictionary<int, string>();
        public int Triangles { get; private set; }
        /// <summary>All instances, batch after batch.</summary>
        public float[] Instances { get; private set; } = Array.Empty<float>();
        /// <summary>Goes up when models or texture layers are added (the vertex buffer and textures must go up again).</summary>
        public int GeometryVersion { get; private set; }

        readonly Dictionary<int, ModelBatch> geometry = new Dictionary<int, ModelBatch>();
        readonly Dictionary<(int, int), int> layerOf = new Dictionary<(int, int), int>();
        readonly Dictionary<int, byte[]> bitmaps = new Dictionary<int, byte[]>();
        readonly TypeTable types;
        readonly Func<int, byte[]> readModel, readBitmap;
        int[] slotOf = Array.Empty<int>();

        ModelScene(TypeTable types, Func<int, byte[]> readModel, Func<int, byte[]> readBitmap)
        {
            this.types = types;
            this.readModel = readModel;
            this.readBitmap = readBitmap;
        }

        /// <param name="readModel">Reads a sappear.flx record (null if empty).</param>
        /// <param name="readBitmap">Reads a bitmap16.flx record (null if empty).</param>
        public static ModelScene Build(SceneObjects objects, MapSpace space, TypeTable types,
                                       Func<int, byte[]> readModel, Func<int, byte[]> readBitmap)
        {
            var scene = new ModelScene(types, readModel, readBitmap);
            scene.RebuildInstances(objects, space);
            return scene;
        }

        /// <summary>The model an object shows, or -1.</summary>
        public int ModelOf(MapObject o)
        {
            int type = o.Type;
            if (type == TypeTable.PlaceholderType) return -1;
            int model = o.Model >= 0 ? o.Model : types[type].DefaultModel;
            return model > 0 ? model : -1;
        }

        /// <summary>The geometry of a model, read on first use; null if it cannot be read.</summary>
        public ModelBatch Geometry(int model)
        {
            if (model <= 0) return null;
            if (geometry.TryGetValue(model, out var b)) return b;
            b = null;
            try
            {
                var rec = readModel(model);
                if (rec == null || !SappearModel.IsModel(rec)) Failed[model] = "not a model record";
                else
                {
                    var tris = SappearModel.Triangles(rec, 0);
                    growing ??= new List<float>(vertices);
                    int first = growing.Count / VertexFloats;
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    tris.RemoveAll(t => t.Invisible);      // blocking walls, drawn magenta otherwise
                    foreach (var t in tris)
                    {
                        int layer = Layer(t.Texture, t.Frame);
                        float cr = (t.Colour & 0xFF) / 255f, cg = ((t.Colour >> 8) & 0xFF) / 255f, cb = ((t.Colour >> 16) & 0xFF) / 255f;
                        Add(growing, t.P0, t.N0, t.T0, layer, cr, cg, cb);
                        Add(growing, t.P1, t.N1, t.T1, layer, cr, cg, cb);
                        Add(growing, t.P2, t.N2, t.T2, layer, cr, cg, cb);
                        min = Vector3.Min(min, Vector3.Min(t.P0, Vector3.Min(t.P1, t.P2)));
                        max = Vector3.Max(max, Vector3.Max(t.P0, Vector3.Max(t.P1, t.P2)));
                    }
                    if (tris.Count == 0) { min = max = Vector3.Zero; }
                    Triangles += tris.Count;
                    b = new ModelBatch { Model = model, FirstVertex = first, VertexCount = tris.Count * 3, BoxMin = min, BoxMax = max };
                    GeometryVersion++;
                }
            }
            catch (System.IO.InvalidDataException e) { Failed[model] = e.Message; }
            geometry[model] = b;
            return b;
        }

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
                    Layers.Add(Resample(f, LayerSize));
                    l = Layers.Count - 1;
                }
                catch (System.IO.InvalidDataException) { }
            }
            layerOf[(tex, frame)] = l;
            return l;
        }

        /// <summary>Groups the objects (not removed ones) by model again and recomputes every instance.</summary>
        public void RebuildInstances(SceneObjects objects, MapSpace space)
        {
            foreach (var g in geometry.Values) g?.Users.Clear();
            foreach (var o in objects.All)
            {
                o.Model = -1;
                if (o.Source.Removed) continue;
                var g = Geometry(ModelOf(o.Source));
                if (g == null || g.VertexCount == 0) continue;
                o.Model = g.Model;
                g.Users.Add(o);
            }
            if (growing != null) { vertices = growing.ToArray(); growing = null; }
            bitmaps.Clear();

            Batches.Clear();
            foreach (var g in geometry.Values) if (g != null && g.Users.Count > 0) Batches.Add(g);
            Batches.Sort((a, b) => a.FirstVertex.CompareTo(b.FirstVertex));
            int total = 0;
            foreach (var b in Batches) { b.InstanceFirst = total; b.InstanceCount = b.Users.Count; total += b.Users.Count; }
            Instances = new float[total * ModelBatch.InstanceFloats];
            slotOf = new int[objects.All.Count];
            Array.Fill(slotOf, -1);
            foreach (var b in Batches)
                for (int i = 0; i < b.Users.Count; i++)
                {
                    var o = b.Users[i];
                    slotOf[o.Id] = b.InstanceFirst + i;
                    var m = Placement(o.Source, space);
                    Write(slotOf[o.Id], m, o.Id);
                    Bound(o, b, m);
                }
        }

        /// <summary>The instance slot of an object, or -1 if it shows no model.</summary>
        public int SlotOf(int objectId) => objectId >= 0 && objectId < slotOf.Length ? slotOf[objectId] : -1;

        /// <summary>Sets one object's placement; returns its slot (-1 if it shows no model).</summary>
        public int SetInstance(SceneObject o, Matrix4x4 m)
        {
            int slot = SlotOf(o.Id);
            if (slot < 0) return slot;
            Write(slot, m, o.Id);
            if (geometry.TryGetValue(o.Model, out var g) && g != null) Bound(o, g, m);
            return slot;
        }

        static void Bound(SceneObject o, ModelBatch g, Matrix4x4 m)
        {
            o.BoundCentre = Vector3.Transform((g.BoxMin + g.BoxMax) * 0.5f, m);
            o.BoundRadius = (g.BoxMax - g.BoxMin).Length() * 0.5f / MapSpace.ObjectUnitsPerPoint;
        }

        void Write(int slot, Matrix4x4 m, int id)
        {
            int k = slot * ModelBatch.InstanceFloats;
            var a = Instances;
            a[k] = m.M11; a[k + 1] = m.M12; a[k + 2] = m.M13; a[k + 3] = m.M14;
            a[k + 4] = m.M21; a[k + 5] = m.M22; a[k + 6] = m.M23; a[k + 7] = m.M24;
            a[k + 8] = m.M31; a[k + 9] = m.M32; a[k + 10] = m.M33; a[k + 11] = m.M34;
            a[k + 12] = m.M41; a[k + 13] = m.M42; a[k + 14] = m.M43; a[k + 15] = m.M44;
            a[k + 16] = id;
        }

        /// <summary>Model space to editor world for one object (System.Numerics row-vector convention: v * M).</summary>
        public static Matrix4x4 Placement(MapObject o, MapSpace space) => Placement(o.MapX, o.MapY, o.Z, o.Rotation, space);

        /// <summary>Model space to editor world for an object at a map position (object units) with a rotation.</summary>
        public static Matrix4x4 Placement(float mapX, float mapY, float z, Quaternion q, MapSpace space)
        {
            if (q.LengthSquared() < 1e-6f) q = Quaternion.Identity; else q = Quaternion.Normalize(q);
            var toObject = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(mapX, mapY, z);
            const float s = 1f / MapSpace.ObjectUnitsPerPoint;
            var toWorld = new Matrix4x4(
                s, 0, 0, 0,
                0, 0, space.NorthIsRowZero ? -s : s, 0,
                0, s, 0, 0,
                0, 0, space.NorthIsRowZero ? space.Height : 0, 1);
            return toObject * toWorld;
        }

        /// <summary>
        /// Where a world ray first meets an object's model (distance along the ray, which must be normalised), or
        /// infinity: the ray goes to model space, then against the bounding box, then the triangles.
        /// </summary>
        public float RayHit(SceneObject o, Matrix4x4 placement, Vector3 origin, Vector3 dir)
        {
            if (o.Model < 0 || !geometry.TryGetValue(o.Model, out var g) || g == null) return float.PositiveInfinity;
            if (!Matrix4x4.Invert(placement, out var inv)) return float.PositiveInfinity;
            var o2 = Vector3.Transform(origin, inv);
            var d2 = Vector3.TransformNormal(dir, inv);
            // distances along d2 equal distances along dir, since d2 is dir carried by the same map
            if (!Slab(o2, d2, g.BoxMin, g.BoxMax)) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            var v = vertices;
            for (int i = 0; i < g.VertexCount; i += 3)
            {
                int k = (g.FirstVertex + i) * VertexFloats;
                var a = new Vector3(v[k], v[k + 1], v[k + 2]);
                k += VertexFloats;
                var b = new Vector3(v[k], v[k + 1], v[k + 2]);
                k += VertexFloats;
                var c = new Vector3(v[k], v[k + 1], v[k + 2]);
                float t = Triangle(o2, d2, a, b, c);
                if (t < best) best = t;
            }
            return best;
        }

        static bool Slab(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
        {
            float t0 = 0, t1 = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
                float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z, hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
                if (Math.Abs(di) < 1e-12f) { if (oi < lo || oi > hi) return false; continue; }
                float a = (lo - oi) / di, b = (hi - oi) / di;
                if (a > b) (a, b) = (b, a);
                t0 = Math.Max(t0, a);
                t1 = Math.Min(t1, b);
                if (t0 > t1) return false;
            }
            return true;
        }

        /// <summary>Möller-Trumbore, both sides; infinity for a miss.</summary>
        static float Triangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Math.Abs(det) < 1e-12f) return float.PositiveInfinity;
            float inv = 1 / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return float.PositiveInfinity;
            var q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(d, q) * inv;
            if (w < 0 || u + w > 1) return float.PositiveInfinity;
            float t = Vector3.Dot(e2, q) * inv;
            return t > 0 ? t : float.PositiveInfinity;
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
