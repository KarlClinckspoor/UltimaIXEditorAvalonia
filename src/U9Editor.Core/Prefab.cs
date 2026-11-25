using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using U9.Formats.Objects;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    /// <summary>One object of a prefab: its tree (record, properties, contents) and where it stands in the prefab.</summary>
    public sealed class PrefabObject
    {
        public ObjectFileKind Kind;
        public ObjectTree Tree;
        /// <summary>Position from the prefab's origin (its ground patch's first point), object units (128 a point).</summary>
        public int X, Y;
        /// <summary>Height (object units) and the ground's height under it when it was copied.</summary>
        public int Z, GroundZ;
        public int Type => Tree.Type;
    }

    /// <summary>
    /// A group of objects with the ground under them, to paste elsewhere on the same map or another: a rectangle of
    /// terrain points (heights, and per quad the texture, frame, turn and hole bits), the objects as trees with their
    /// property chains and contents, and the trigger scripts their properties name.
    /// <para>The patch is <see cref="Width"/> x <see cref="Height"/> points; the quads are one fewer each way (a
    /// point's texture bits belong to the quad east and north of it). <see cref="Margin"/> points round the objects'
    /// bounds are the edge a paste can blend into the ground it lands on.</para>
    /// </summary>
    public sealed class Prefab
    {
        public string Name = "";
        public int SourceMap = -1;
        public int Width, Height;
        public int Margin;
        /// <summary>Point heights (0-4095), x + row * Width; null when the prefab has no ground.</summary>
        public int[] Heights;
        /// <summary>Per quad the point bits above the height (hole, turn, bit 15, frame, texture), x + row * (Width - 1); null with no ground.</summary>
        public uint[] Quads;
        public List<PrefabObject> Objects = new List<PrefabObject>();
        /// <summary>The scripts the objects' trigger properties named when copied, by slot.</summary>
        public Dictionary<int, TriggerOp[]> Scripts = new Dictionary<int, TriggerOp[]>();

        public bool HasGround => Heights != null;
        public int QuadWidth => Width - 1;

        static readonly int[] TriggerIds = { PropertySegment.OoTrigger, PropertySegment.SfTrigger };

        /// <summary>The script slots a tree's trigger properties name (0 left out).</summary>
        public static IEnumerable<(int Segment, int Place, int Which, int Slot)> SlotsOf(ObjectTree t) =>
            t.Properties().Where(p => TriggerIds.Contains(p.Id))
             .SelectMany(p => new[] { (p.Segment, p.Place, 0, p.A), (p.Segment, p.Place, 1, p.B) })
             .Where(s => s.Item4 > 0);

        /// <summary>
        /// Copies objects and the ground under them: the patch covers the objects' bounds and <paramref name="margin"/>
        /// points round them (inside the map). Objects whose property chains or contents cannot be read whole still
        /// copy what can be read.
        /// </summary>
        public static Prefab Copy(MapEditor ed, IEnumerable<SceneObject> objects, int margin, bool ground)
        {
            var list = objects.Where(o => o != null && !o.Source.Removed).Distinct().ToList();
            if (list.Count == 0) throw new InvalidOperationException("Nothing is selected to copy.");
            margin = Math.Clamp(margin, 0, 64);
            int w = ed.Raster.Width, h = ed.Raster.Height;
            int x0 = Math.Clamp((int)Math.Floor(list.Min(o => o.Source.MapX) / 128.0) - margin, 0, w - 2);
            int y0 = Math.Clamp((int)Math.Floor(list.Min(o => o.Source.MapY) / 128.0) - margin, 0, h - 2);
            int x1 = Math.Clamp((int)Math.Ceiling(list.Max(o => o.Source.MapX) / 128.0) + margin, x0 + 1, w - 1);
            int y1 = Math.Clamp((int)Math.Ceiling(list.Max(o => o.Source.MapY) / 128.0) + margin, y0 + 1, h - 1);
            var p = new Prefab { SourceMap = ed.MapNumber, Width = x1 - x0 + 1, Height = y1 - y0 + 1, Margin = margin };
            if (ground)
            {
                p.Heights = new int[p.Width * p.Height];
                p.Quads = new uint[(p.Width - 1) * (p.Height - 1)];
                for (int j = 0; j < p.Height; j++)
                    for (int i = 0; i < p.Width; i++)
                    {
                        uint raw = ed.Raster.Points[x0 + i + (y0 + j) * w];
                        p.Heights[i + j * p.Width] = (int)(raw & 0xFFF);
                        if (i < p.Width - 1 && j < p.Height - 1) p.Quads[i + j * (p.Width - 1)] = raw & ~0xFFFu;
                    }
            }
            foreach (var o in list)
            {
                var s = o.Source;
                var po = new PrefabObject
                {
                    Kind = s.Map.Kind, Tree = s.Map.TreeOf(s), X = s.MapX - x0 * 128, Y = s.MapY - y0 * 128,
                    Z = s.Z, GroundZ = ed.GroundZ(s.MapX, s.MapY),
                };
                p.Objects.Add(po);
                if (ed.Scripts != null)
                    foreach (var sl in SlotsOf(po.Tree))
                        if (!p.Scripts.ContainsKey(sl.Slot) && ed.Scripts.IsUsed(sl.Slot)) p.Scripts[sl.Slot] = ed.Scripts[sl.Slot];
            }
            var names = list.Where(o => !string.IsNullOrWhiteSpace(o.Name)).GroupBy(o => o.Name).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(2).ToList();
            if (names.Count == 0) names.Add($"type {list[0].Source.Type}");
            p.Name = string.Join(", ", names) + (list.Count > 1 ? $" ({list.Count} objects)" : "");
            return p;
        }

        /// <summary>The prefab turned counter-clockwise (seen from above) by quarter turns: ground, positions and facings.</summary>
        public Prefab Turned(int quarterTurns)
        {
            var p = this;
            for (int k = ((quarterTurns % 4) + 4) % 4; k > 0; k--) p = p.TurnedOnce();
            return p;
        }

        // (x, row) -> (H - 1 - row, x): east goes north. A quad's texture turns with it: one turn less (the turn bits
        // count clockwise quarter turns, see TerrainPoint.Rotation).
        Prefab TurnedOnce()
        {
            int W = Width, H = Height;
            var p = new Prefab { Name = Name, SourceMap = SourceMap, Width = H, Height = W, Margin = Margin, Scripts = Scripts };
            if (HasGround)
            {
                p.Heights = new int[W * H];
                for (int j = 0; j < H; j++)
                    for (int i = 0; i < W; i++) p.Heights[(H - 1 - j) + i * H] = Heights[i + j * W];
                p.Quads = new uint[(W - 1) * (H - 1)];
                for (int j = 0; j < H - 1; j++)
                    for (int i = 0; i < W - 1; i++)
                    {
                        uint q = Quads[i + j * (W - 1)];
                        uint r = ((q >> 13) + 3) & 3;
                        p.Quads[(H - 2 - j) + i * (H - 1)] = (q & ~(3u << 13)) | (r << 13);
                    }
            }
            foreach (var o in Objects)
            {
                var t = o.Tree.Clone();
                SetRotation(t.Record, MapEditor.Turned(RotationOf(t.Record), 90));
                p.Objects.Add(new PrefabObject { Kind = o.Kind, Tree = t, X = (H - 1) * 128 - o.Y, Y = o.X, Z = o.Z, GroundZ = o.GroundZ });
            }
            return p;
        }

        public static Quaternion RotationOf(byte[] rec) => new Quaternion(
            BinaryPrimitives.ReadInt16LittleEndian(rec.AsSpan(0x0C)) / 32767f, BinaryPrimitives.ReadInt16LittleEndian(rec.AsSpan(0x0E)) / 32767f,
            BinaryPrimitives.ReadInt16LittleEndian(rec.AsSpan(0x10)) / 32767f, BinaryPrimitives.ReadInt16LittleEndian(rec.AsSpan(0x12)) / 32767f);

        static void SetRotation(byte[] rec, Quaternion q)
        {
            q = q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(q);
            if (q.W < 0) q = -q;
            static short Q(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), -32767, 32767);
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(0x0C), Q(q.X));
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(0x0E), Q(q.Y));
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(0x10), Q(q.Z));
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(0x12), Q(q.W));
        }

        public string Describe()
        {
            int f = Objects.Count(o => o.Kind == ObjectFileKind.Fixed), n = Objects.Count - f;
            string ground = HasGround ? $"{Width} x {Height} points of ground" : $"no ground ({Width} x {Height} points)";
            string scripts = Scripts.Count == 0 ? "" : Scripts.Count == 1 ? ", 1 script" : $", {Scripts.Count} scripts";
            return $"{Objects.Count} objects ({f} fixed, {n} nonfixed), {ground}{scripts}, from map {SourceMap}";
        }

        // ---- files

        public const string Extension = ".u9prefab";

        public void Save(string path) => File.WriteAllText(path, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        public static Prefab Load(string path) => FromJson(JsonNode.Parse(File.ReadAllText(path)));

        public JsonObject ToJson()
        {
            var o = new JsonObject
            {
                ["format"] = "u9prefab", ["version"] = 1, ["name"] = Name, ["sourceMap"] = SourceMap,
                ["width"] = Width, ["height"] = Height, ["margin"] = Margin,
            };
            if (HasGround)
            {
                o["heights"] = new JsonArray(Heights.Select(h => (JsonNode)h).ToArray());
                var bytes = new byte[Quads.Length * 4];
                for (int i = 0; i < Quads.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), Quads[i]);
                o["quads"] = Convert.ToBase64String(bytes);
            }
            o["objects"] = new JsonArray(Objects.Select(po => (JsonNode)new JsonObject
            {
                ["kind"] = po.Kind == ObjectFileKind.Fixed ? "fixed" : "nonfixed", ["type"] = po.Type,
                ["x"] = po.X, ["y"] = po.Y, ["z"] = po.Z, ["ground"] = po.GroundZ, ["tree"] = TreeJson(po.Tree),
            }).ToArray());
            var scripts = new JsonObject();
            foreach (var kv in Scripts.OrderBy(k => k.Key))
                scripts[kv.Key.ToString()] = new JsonArray(kv.Value.Select(op => (JsonNode)new JsonArray(op.Op, op.A, op.B)).ToArray());
            o["scripts"] = scripts;
            return o;
        }

        static JsonObject TreeJson(ObjectTree t) => new JsonObject
        {
            ["record"] = Convert.ToHexString(t.Record),
            ["segments"] = new JsonArray(t.Segments.Select(s => (JsonNode)Convert.ToHexString(s)).ToArray()),
            ["contents"] = new JsonArray(t.Contents.Select(c => (JsonNode)new JsonObject
            {
                ["segment"] = c.Segment, ["place"] = c.Place,
                ["items"] = new JsonArray(c.Items.Select(i => (JsonNode)TreeJson(i)).ToArray()),
            }).ToArray()),
        };

        public static Prefab FromJson(JsonNode n)
        {
            if ((string)n["format"] != "u9prefab") throw new InvalidDataException("Not a U9 Editor prefab");
            if ((int)n["version"] != 1) throw new InvalidDataException($"Prefab version {(int)n["version"]} is newer than this editor reads");
            var p = new Prefab
            {
                Name = (string)n["name"] ?? "", SourceMap = (int?)n["sourceMap"] ?? -1,
                Width = (int)n["width"], Height = (int)n["height"], Margin = (int?)n["margin"] ?? 0,
            };
            if (p.Width < 2 || p.Height < 2 || p.Width > 4096 || p.Height > 4096) throw new InvalidDataException("The prefab's size is out of range");
            if (n["heights"] is JsonArray hs)
            {
                p.Heights = hs.Select(h => Math.Clamp((int)h, 0, 0xFFF)).ToArray();
                var bytes = Convert.FromBase64String((string)n["quads"]);
                p.Quads = new uint[bytes.Length / 4];
                for (int i = 0; i < p.Quads.Length; i++) p.Quads[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 * i)) & ~0xFFFu;
                if (p.Heights.Length != p.Width * p.Height || p.Quads.Length != (p.Width - 1) * (p.Height - 1))
                    throw new InvalidDataException("The prefab's ground does not match its size");
            }
            foreach (var on in n["objects"].AsArray())
            {
                var kind = (string)on["kind"] == "fixed" ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;
                var tree = TreeFrom(on["tree"], kind == ObjectFileKind.Fixed ? 24 : 32);
                p.Objects.Add(new PrefabObject { Kind = kind, Tree = tree, X = (int)on["x"], Y = (int)on["y"], Z = (int)on["z"], GroundZ = (int)on["ground"] });
            }
            if (n["scripts"] is JsonObject so)
                foreach (var kv in so)
                    p.Scripts[int.Parse(kv.Key)] = kv.Value.AsArray().Select(op => new TriggerOp((ushort)(int)op[0], (ushort)(int)op[1], (ushort)(int)op[2])).ToArray();
            return p;
        }

        static ObjectTree TreeFrom(JsonNode n, int recordSize)
        {
            var t = new ObjectTree { Record = Convert.FromHexString((string)n["record"]) };
            if (t.Record.Length != recordSize) throw new InvalidDataException($"A prefab record is {t.Record.Length} bytes, not {recordSize}");
            foreach (var s in n["segments"]?.AsArray() ?? new JsonArray())
            {
                var b = Convert.FromHexString((string)s);
                if (b.Length != 16) throw new InvalidDataException("A property segment is 16 bytes");
                t.Segments.Add(b);
            }
            foreach (var c in n["contents"]?.AsArray() ?? new JsonArray())
                t.Contents.Add(new TreeContents
                {
                    Segment = (int)c["segment"], Place = (int)c["place"],
                    Items = c["items"].AsArray().Select(i => TreeFrom(i, recordSize)).ToList(),
                });
            return t;
        }
    }
}
