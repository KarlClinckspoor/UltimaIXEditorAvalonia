using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using U9.Formats.Bitmaps;
using U9.Formats.Flx;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    public sealed class WebExportOptions
    {
        /// <summary>Map numbers to bake; null for every map with terrain.</summary>
        public int[] Maps { get; set; }
        /// <summary>Largest model texture side; larger ones are box-filtered down.</summary>
        public int TextureMax { get; set; } = 128;
        public bool Models { get; set; } = true;
    }

    /// <summary>What an export wrote: bytes per file kind, per map and shared, and the counts it checked.</summary>
    public sealed class WebExportReport
    {
        public readonly SortedDictionary<int, SortedDictionary<string, long>> MapBytes = new SortedDictionary<int, SortedDictionary<string, long>>();
        public readonly SortedDictionary<string, long> SharedBytes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        public readonly SortedDictionary<int, (int Objects, int Drawn, int Links, int Resolved)> Counts = new SortedDictionary<int, (int, int, int, int)>();
        public int TerrainLayers, ModelLayers, Models;
        public readonly List<string> Problems = new List<string>();

        public long Total => SharedBytes.Values.Sum() + MapBytes.Values.Sum(m => m.Values.Sum());

        public override string ToString()
        {
            var sb = new StringBuilder();
            static string Mb(long b) => (b / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            sb.AppendLine($"terrain layers {TerrainLayers}, model layers {ModelLayers}, models {Models}");
            sb.AppendLine("shared:");
            // PNGs of one array summed: textures/model64-3.png -> textures/model64-*.png
            foreach (var g in SharedBytes.GroupBy(kv => System.Text.RegularExpressions.Regex.Replace(kv.Key, @"-\d+\.png$", "-*.png")).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.AppendLine($"  {g.Key,-24} {Mb(g.Sum(kv => kv.Value)),12}  ({g.Count()} file{(g.Count() == 1 ? "" : "s")})");
            foreach (var m in MapBytes)
            {
                var c = Counts[m.Key];
                sb.AppendLine($"map {m.Key}: {Mb(m.Value.Values.Sum())}  ({c.Objects} objects, {c.Drawn} drawn, {c.Links} links, {c.Resolved} resolved)");
                foreach (var kv in m.Value) sb.AppendLine($"  {kv.Key,-24} {Mb(kv.Value),12}");
            }
            sb.AppendLine($"total {Mb(Total)}");
            foreach (var p in Problems) sb.AppendLine("problem: " + p);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Bakes maps for the WebGL viewer in UltimaCartographer/u9viewer (its EXPORT.md is the contract): terrain as
    /// chunks with re-coded points, every model the maps use in world axes, texture layers as stacked PNGs, objects
    /// grouped by model, and map links. The output depends only on the input: every list is written in a sorted
    /// order and the PNG and JSON writers add nothing of their own.
    /// </summary>
    public static class WebExport
    {
        public const int TerrainLayerSize = 128, LayersPerFile = 64;
        /// <summary>Invisible helper types: trigger zones and arrival markers. Listed, never drawn.</summary>
        static readonly HashSet<int> HelperTypes = new HashSet<int> { 1111, 1134 };

        sealed class MapData
        {
            public MapEntry Entry;
            public TerrainMap Terrain;
            public ObjectMap Fixed, Nonfixed;
            public List<MapObject> Objects;     // fixed first, then nonfixed, each in file order
        }

        sealed class ModelLayer
        {
            public int Size, Array, Index;
            public byte Mode;
            public byte[] Rgba;
        }

        public static WebExportReport Run(GameFolder game, string outDir, WebExportOptions options, Action<string> log = null)
        {
            log ??= _ => { };
            var report = new WebExportReport();
            string Static(string name) => Directory.EnumerateFiles(game.StaticFolder, name, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            using var bitmaps = FlxArchive.Open(Static("bitmap16.flx"));
            using var appearances = FlxArchive.Open(Static("sappear.flx"));
            var types = TypeTable.Load(Static("types.dat"));
            var names = Static("typename.flx") is string tn ? TypeNames.Load(tn) : null;
            var triggers = Static("triggers.flx") is string tr ? TriggerScripts.Load(tr) : null;
            byte[] Bitmap(int i) => i >= 0 && i < bitmaps.Count ? bitmaps.Read(i) : null;
            byte[] Appearance(int i) => i >= 0 && i < appearances.Count ? appearances.Read(i) : null;

            // ---- maps and their objects
            var objectCache = new Dictionary<(int, ObjectFileKind), ObjectMap>();
            ObjectMap Objects(int map, ObjectFileKind kind)
            {
                if (objectCache.TryGetValue((map, kind), out var m)) return m;
                string dir = Path.Combine(game.Root, kind == ObjectFileKind.Fixed ? "static" : "runtime");
                string f = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, (kind == ObjectFileKind.Fixed ? "fixed." : "nonfixed.") + map,
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault() : null;
                try { m = f != null ? ObjectMap.Load(f, kind) : null; }
                catch (InvalidDataException) { m = null; }
                return objectCache[(map, kind)] = m;
            }
            List<MapObject> AllObjects(int map) =>
                (Objects(map, ObjectFileKind.Fixed)?.Objects ?? new List<MapObject>()).Concat(Objects(map, ObjectFileKind.Nonfixed)?.Objects ?? new List<MapObject>()).ToList();

            var maps = new List<MapData>();
            foreach (var e in game.Maps().OrderBy(m => m.Number))
            {
                if (options.Maps != null && !options.Maps.Contains(e.Number)) continue;
                var t = TerrainMap.Load(e.TerrainPath);
                if (t.Width == 0 || t.Height == 0) { log($"map {e.Number}: no terrain, skipped"); continue; }
                maps.Add(new MapData
                {
                    Entry = e, Terrain = t, Fixed = Objects(e.Number, ObjectFileKind.Fixed), Nonfixed = Objects(e.Number, ObjectFileKind.Nonfixed),
                    Objects = AllObjects(e.Number),
                });
            }
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(Path.Combine(outDir, "textures"));

            // ---- terrain layers: every (texture, frame) any exported map uses
            var terrainKeys = new SortedSet<int>();
            foreach (var m in maps)
                foreach (var c in m.Terrain.Chunks)
                    foreach (uint raw in c.Points) terrainKeys.Add(Key(raw));
            var terrainLayer = new Dictionary<int, int>();
            var terrainPixels = new List<byte[]>();
            var records = new Dictionary<int, byte[]>();
            foreach (int key in terrainKeys)
            {
                var f = Frame(key >> 6, key & 63, Bitmap, records);
                if (f == null) continue;
                terrainLayer[key] = terrainPixels.Count;
                terrainPixels.Add(Resample(f, TerrainLayerSize));
            }
            records.Clear();
            var terrainFiles = WriteLayers(outDir, "terrain", TerrainLayerSize, terrainPixels, report);
            report.TerrainLayers = terrainPixels.Count;
            log($"terrain: {terrainPixels.Count} layers");

            // ---- models
            int ModelOf(MapObject o)
            {
                if (o.Type == TypeTable.PlaceholderType || HelperTypes.Contains(o.Type)) return -1;
                int model = o.Model >= 0 ? o.Model : types[o.Type].DefaultModel;
                return model > 0 ? model : -1;
            }
            var modelTris = new SortedDictionary<int, List<List<ModelTriangle>>>();     // model -> LODs (empty ones kept here)
            var modelThresholds = new Dictionary<int, uint[]>();
            if (options.Models)
            {
                foreach (int model in maps.SelectMany(m => m.Objects).Select(ModelOf).Where(x => x > 0).Distinct().OrderBy(x => x))
                {
                    var rec = Appearance(model);
                    if (rec == null || !SappearModel.IsModel(rec)) continue;
                    var lods = new List<List<ModelTriangle>>();
                    try
                    {
                        for (int lod = 0; lod < SappearModel.LodCount(rec); lod++)
                            lods.Add(SappearModel.Triangles(rec, lod).Where(t => !t.Invisible).ToList());
                    }
                    catch (InvalidDataException ex) { report.Problems.Add($"model {model}: {ex.Message}"); continue; }
                    if (lods.Count == 0 || lods[0].Count == 0) continue;
                    modelTris[model] = lods;
                    modelThresholds[model] = SappearModel.LodThresholds(rec);
                }
            }

            // model texture layers, one array per side, sides ascending, layers by (texture, frame) within a side
            var modelLayers = new SortedDictionary<int, ModelLayer>();
            foreach (int key in modelTris.Values.SelectMany(l => l).SelectMany(l => l).Where(t => t.Texture >= 0).Select(t => t.Texture * 64 + Math.Min(t.Frame, 63)).Distinct().OrderBy(k => k))
            {
                var f = Frame(key >> 6, key & 63, Bitmap, records, clampFrame: true);
                if (f == null) continue;
                int side = Side(Math.Max(f.Width, f.Height), options.TextureMax);
                modelLayers[key] = new ModelLayer
                {
                    Size = side, Rgba = Resample(f, side),
                    Mode = f.Format == 3 ? (byte)2 : f.Format == 5 || f.HasTransparency ? (byte)1 : (byte)0,
                };
            }
            records.Clear();
            var sides = modelLayers.Values.Select(l => l.Size).Distinct().OrderBy(s => s).ToList();
            var modelTextureJson = new List<(int Size, List<string> Files, int Count, string Modes)>();
            for (int a = 0; a < sides.Count; a++)
            {
                var layers = modelLayers.Where(kv => kv.Value.Size == sides[a]).OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                if (layers.Count > 4096) report.Problems.Add($"model array {sides[a]} has {layers.Count} layers, over the 4096 a texture reference can name");
                for (int i = 0; i < layers.Count; i++) { layers[i].Array = a; layers[i].Index = i; }
                var files = WriteLayers(outDir, "model" + sides[a], sides[a], layers.Select(l => l.Rgba).ToList(), report);
                string modes = $"textures/model{sides[a]}.modes";
                File.WriteAllBytes(Path.Combine(outDir, modes), layers.Select(l => l.Mode).ToArray());
                report.SharedBytes["textures/model" + sides[a] + ".modes"] = layers.Count;
                modelTextureJson.Add((sides[a], files, layers.Count, modes));
            }
            report.ModelLayers = modelLayers.Count;

            WriteModels(outDir, modelTris, modelThresholds, modelLayers, report);
            report.Models = modelTris.Count;
            log($"models: {modelTris.Count}, {modelLayers.Count} texture layers in {sides.Count} sizes");

            // ---- per map
            var links = triggers != null ? new MapLinks(triggers, n => Objects(n, ObjectFileKind.Nonfixed)) : null;
            var manifestMaps = new List<Action<Utf8JsonWriter>>();
            foreach (var m in maps)
            {
                int n = m.Entry.Number;
                string dir = Path.Combine(outDir, "maps", n.ToString());
                Directory.CreateDirectory(dir);
                var bytes = report.MapBytes[n] = new SortedDictionary<string, long>(StringComparer.Ordinal);

                bytes["terrain.bin"] = WriteTerrain(Path.Combine(dir, "terrain.bin"), m.Terrain, terrainLayer, report, n);

                var index = new Dictionary<MapObject, int>();
                for (int i = 0; i < m.Objects.Count; i++) index[m.Objects[i]] = i;
                var drawn = m.Objects.Select((o, i) => (o, i, model: ModelOf(o))).Where(x => x.model > 0 && modelTris.ContainsKey(x.model))
                                     .OrderBy(x => x.model).ThenBy(x => x.i).ToList();
                var drawnSet = new HashSet<int>(drawn.Select(x => x.i));
                bytes["objects.bin"] = WriteObjectsBin(Path.Combine(dir, "objects.bin"), drawn.Select(x => (x.o, x.i, x.model)).ToList());
                bytes["objects.json"] = WriteJson(Path.Combine(dir, "objects.json"), w =>
                {
                    w.WriteStartArray();
                    for (int i = 0; i < m.Objects.Count; i++)
                    {
                        var o = m.Objects[i];
                        var ti = types[o.Type];
                        w.WriteStartObject();
                        w.WriteNumber("type", o.Type);
                        if (names?[o.Type] is string nm) w.WriteString("name", nm);
                        w.WriteBoolean("fixed", o.Map.Kind == ObjectFileKind.Fixed);
                        w.WriteBoolean("drawn", drawnSet.Contains(i));
                        w.WriteNumber("x", Math.Round(o.PointX, 4));
                        w.WriteNumber("row", Math.Round(o.PointRow, 4));
                        w.WriteNumber("height", o.TerrainHeight);
                        w.WriteNumber("link", Math.Max(0, o.Link));
                        w.WriteNumber("book", ti.Book);
                        int model = o.Model >= 0 ? o.Model : ti.DefaultModel;
                        w.WriteNumber("model", model);
                        w.WriteNumber("flags", o.Flags);
                        w.WriteNumber("usecode", ti.Usecode);
                        w.WriteNumber("weight", ti.Weight);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });

                var mapLinks = links?.From(n) ?? new List<MapLink>();
                bytes["links.json"] = WriteJson(Path.Combine(dir, "links.json"), w =>
                {
                    w.WriteStartArray();
                    foreach (var l in mapLinks.OrderBy(l => index.TryGetValue(l.Zone, out int i) ? i : int.MaxValue).ThenBy(l => l.TriggerSlot).ThenBy(l => l.DestinationMap))
                    {
                        w.WriteStartObject();
                        w.WriteNumber("object", index[l.Zone]);
                        w.WriteString("script", l.Ops);
                        w.WriteNumber("trigger", l.TriggerSlot);
                        w.WriteNumber("toMap", l.DestinationMap);
                        w.WriteNumber("toLink", l.DestinationLink);
                        w.WriteNumber("markerType", l.MarkerType);
                        var a = l.Arrival;
                        if (a != null)
                        {
                            w.WriteNumber("toX", Math.Round(a.PointX, 4));
                            w.WriteNumber("toRow", Math.Round(a.PointRow, 4));
                            w.WriteNumber("toHeight", a.TerrainHeight);
                            int fixedCount = Objects(l.DestinationMap, ObjectFileKind.Fixed)?.Objects.Count ?? 0;
                            int at = Objects(l.DestinationMap, ObjectFileKind.Nonfixed)?.Objects.IndexOf(a.Object) ?? -1;
                            if (at >= 0) w.WriteNumber("toObject", fixedCount + at); else w.WriteNull("toObject");
                        }
                        else w.WriteNull("toObject");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
                report.Counts[n] = (m.Objects.Count, drawn.Count, mapLinks.Count, mapLinks.Count(l => l.Resolved));

                var t = m.Terrain;
                manifestMaps.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", n);
                    w.WriteString("name", t.Name.Trim());
                    w.WriteNumber("width", t.Width);
                    w.WriteNumber("height", t.Height);
                    w.WriteNumber("tilesX", t.TilesX);
                    w.WriteNumber("tilesY", t.TilesY);
                    w.WriteNumber("waterHeight", t.WaterHeight);
                    w.WriteNumber("waveAmplitude", t.WaveAmplitude);
                    w.WriteNumber("objects", m.Objects.Count);
                    w.WriteNumber("links", mapLinks.Count);
                    w.WriteEndObject();
                });
                log($"map {n} {t.Name.Trim()}: {m.Objects.Count} objects ({drawn.Count} drawn), {mapLinks.Count} links");
            }

            report.SharedBytes["manifest.json"] = WriteJson(Path.Combine(outDir, "manifest.json"), w =>
            {
                w.WriteStartObject();
                w.WriteString("format", "u9web");
                w.WriteNumber("version", 1);
                w.WriteStartObject("textures");
                w.WriteStartObject("terrain");
                w.WriteNumber("size", TerrainLayerSize);
                w.WriteStartArray("files"); foreach (var f in terrainFiles) w.WriteStringValue(f); w.WriteEndArray();
                w.WriteNumber("count", terrainPixels.Count);
                w.WriteEndObject();
                w.WriteStartArray("model");
                foreach (var mt in modelTextureJson)
                {
                    w.WriteStartObject();
                    w.WriteNumber("size", mt.Size);
                    w.WriteStartArray("files"); foreach (var f in mt.Files) w.WriteStringValue(f); w.WriteEndArray();
                    w.WriteNumber("count", mt.Count);
                    w.WriteString("modes", mt.Modes);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartArray("maps");
                foreach (var a in manifestMaps) a(w);
                w.WriteEndArray();
                w.WriteEndObject();
            });
            return report;
        }

        static int Key(uint raw) => (int)(raw >> 22) * 64 + (int)((raw >> 16) & 63);

        static BitmapFrame Frame(int tex, int frame, Func<int, byte[]> read, Dictionary<int, byte[]> cache, bool clampFrame = false)
        {
            if (!cache.TryGetValue(tex, out var rec)) cache[tex] = rec = read(tex);
            int count = rec != null ? Bitmap16.FrameCount(rec) : 0;
            if (count <= 0) return null;
            if (frame >= count) { if (!clampFrame) return null; frame = count - 1; }
            try { return Bitmap16.ReadFrame(rec, frame); }
            catch (InvalidDataException) { return null; }
        }

        /// <summary>The power of two nearest a texture's larger side, at most <paramref name="max"/>, at least 8.</summary>
        static int Side(int size, int max)
        {
            int p = 8;
            while (p * 2 <= max && Math.Abs(p * 2 - size) <= Math.Abs(p - size)) p *= 2;
            return Math.Min(p, Math.Max(8, max));
        }

        /// <summary>To size x size: a box filter where it shrinks, nearest pixel where it grows.</summary>
        static byte[] Resample(BitmapFrame f, int size)
        {
            if (f.Width == size && f.Height == size) return f.Rgba;
            var o = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int x0 = x * f.Width / size, x1 = Math.Max(x0 + 1, (x + 1) * f.Width / size);
                    int y0 = y * f.Height / size, y1 = Math.Max(y0 + 1, (y + 1) * f.Height / size);
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int sy = y0; sy < y1; sy++)
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int i = (sx + sy * f.Width) * 4;
                            r += f.Rgba[i]; g += f.Rgba[i + 1]; b += f.Rgba[i + 2]; a += f.Rgba[i + 3]; n++;
                        }
                    int k = (x + y * size) * 4;
                    o[k] = (byte)(r / n); o[k + 1] = (byte)(g / n); o[k + 2] = (byte)(b / n); o[k + 3] = (byte)(a / n);
                }
            return o;
        }

        /// <summary>Layers of one size stacked top to bottom, 64 a PNG; returns the files written (relative to the output).</summary>
        static List<string> WriteLayers(string outDir, string stem, int size, List<byte[]> layers, WebExportReport report)
        {
            var files = new List<string>();
            for (int first = 0, k = 0; first < layers.Count; first += LayersPerFile, k++)
            {
                int n = Math.Min(LayersPerFile, layers.Count - first);
                var px = new byte[size * size * 4 * n];
                for (int i = 0; i < n; i++) Buffer.BlockCopy(layers[first + i], 0, px, i * size * size * 4, size * size * 4);
                string rel = $"textures/{stem}-{k}.png";
                PngWriter.WriteRgba(Path.Combine(outDir, rel), size, size * n, px);
                files.Add(rel);
                report.SharedBytes[rel] = new FileInfo(Path.Combine(outDir, rel)).Length;
            }
            return files;
        }

        static long WriteTerrain(string path, TerrainMap t, Dictionary<int, int> layerOf, WebExportReport report, int map)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((uint)t.Chunks.Count);
                foreach (var c in t.Chunks)
                    foreach (uint raw in c.Points)
                    {
                        uint layer = layerOf.TryGetValue(Key(raw), out int l) ? (uint)l : 0xFFFF;
                        if (layer > 0xFFFF) layer = 0xFFFF;
                        w.Write((raw & 0x7FFFu) | (layer << 16));      // height, hole, rotation; bit 15 dropped
                    }
                foreach (ushort chunk in t.TileChunks)
                {
                    if (chunk >= t.Chunks.Count) report.Problems.Add($"map {map}: a tile names chunk {chunk} of {t.Chunks.Count}");
                    w.Write(chunk);
                }
            }
            return new FileInfo(path).Length;
        }

        /// <summary>Model axes (x east, y north, z up, object units) to world axes (x east, y up, z north, points).</summary>
        static Vector3 World(Vector3 v) => new Vector3(v.X, v.Z, v.Y) / MapSpace.ObjectUnitsPerPoint;

        static void WriteModels(string outDir, SortedDictionary<int, List<List<ModelTriangle>>> models, Dictionary<int, uint[]> thresholds,
                                SortedDictionary<int, ModelLayer> layers, WebExportReport report)
        {
            var vertices = new MemoryStream();
            var indices = new List<uint>();
            var vbuf = new byte[32];
            var json = new List<Action<Utf8JsonWriter>>();
            foreach (var kv in models)
            {
                int model = kv.Key;
                var lodJson = new List<(int FirstIndex, int Count, int BaseVertex, bool Blended)>();
                Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
                for (int lod = 0; lod < kv.Value.Count; lod++)
                {
                    var tris = kv.Value[lod];
                    if (tris.Count == 0) continue;
                    int baseVertex = (int)(vertices.Length / 32), firstIndex = indices.Count;
                    var seen = new Dictionary<string, uint>(StringComparer.Ordinal);
                    bool blended = false;
                    foreach (var t in tris)
                    {
                        ushort tex = 0xFFFF;
                        if (t.Texture >= 0 && layers.TryGetValue(t.Texture * 64 + Math.Min(t.Frame, 63), out var ml))
                        {
                            tex = (ushort)((ml.Array << 12) | ml.Index);
                            blended |= ml.Mode == 2;
                        }
                        uint c = t.Colour;
                        ushort rgb565 = (ushort)((((c & 0xFF) >> 3) << 11) | ((((c >> 8) & 0xFF) >> 2) << 5) | (((c >> 16) & 0xFF) >> 3));
                        byte alpha = (byte)(c >> 24);
                        foreach (var (p, nrm, uv) in new[] { (t.P0, t.N0, t.T0), (t.P1, t.N1, t.T1), (t.P2, t.N2, t.T2) })
                        {
                            var wp = World(p);
                            var wn = new Vector3(nrm.X, nrm.Z, nrm.Y);
                            if (lod == 0) { min = Vector3.Min(min, wp); max = Vector3.Max(max, wp); }
                            BinaryPrimitives.WriteSingleLittleEndian(vbuf.AsSpan(0), wp.X);
                            BinaryPrimitives.WriteSingleLittleEndian(vbuf.AsSpan(4), wp.Y);
                            BinaryPrimitives.WriteSingleLittleEndian(vbuf.AsSpan(8), wp.Z);
                            vbuf[12] = (byte)(sbyte)Math.Clamp((int)MathF.Round(wn.X * 127), -127, 127);
                            vbuf[13] = (byte)(sbyte)Math.Clamp((int)MathF.Round(wn.Y * 127), -127, 127);
                            vbuf[14] = (byte)(sbyte)Math.Clamp((int)MathF.Round(wn.Z * 127), -127, 127);
                            vbuf[15] = tex == 0xFFFF ? alpha : (byte)0;
                            BinaryPrimitives.WriteSingleLittleEndian(vbuf.AsSpan(16), uv.X);
                            BinaryPrimitives.WriteSingleLittleEndian(vbuf.AsSpan(20), uv.Y);
                            BinaryPrimitives.WriteUInt16LittleEndian(vbuf.AsSpan(24), tex);
                            BinaryPrimitives.WriteUInt16LittleEndian(vbuf.AsSpan(26), tex == 0xFFFF ? rgb565 : (ushort)0);
                            BinaryPrimitives.WriteUInt32LittleEndian(vbuf.AsSpan(28), 0);
                            string key = Convert.ToBase64String(vbuf);
                            if (!seen.TryGetValue(key, out uint vi))
                            {
                                vi = (uint)seen.Count;
                                seen[key] = vi;
                                vertices.Write(vbuf);
                            }
                            indices.Add(vi);
                        }
                    }
                    lodJson.Add((firstIndex, indices.Count - firstIndex, baseVertex, blended));
                }
                var th = thresholds[model];
                json.Add(w =>
                {
                    w.WriteStartObject(model.ToString());
                    w.WriteStartArray("bbox");
                    foreach (float f in new[] { min.X, min.Y, min.Z, max.X, max.Y, max.Z }) w.WriteNumberValue(Math.Round(f, 4));
                    w.WriteEndArray();
                    w.WriteStartArray("lodDistances");
                    foreach (uint t in th)
                        if (t == 0 || t >= 0x80000000u) w.WriteNullValue();
                        else w.WriteNumberValue(Math.Round(Math.Sqrt(t) / MapSpace.ObjectUnitsPerPoint, 3));
                    w.WriteEndArray();
                    w.WriteStartArray("lods");
                    foreach (var l in lodJson)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("firstIndex", l.FirstIndex);
                        w.WriteNumber("indexCount", l.Count);
                        w.WriteNumber("baseVertex", l.BaseVertex);
                        w.WriteBoolean("blended", l.Blended);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
            }
            using (var f = File.Create(Path.Combine(outDir, "models.bin")))
            {
                vertices.Position = 0;
                vertices.CopyTo(f);
                var ib = new byte[4];
                foreach (uint i in indices) { BinaryPrimitives.WriteUInt32LittleEndian(ib, i); f.Write(ib); }
            }
            long vertexBytes = vertices.Length;
            report.SharedBytes["models.bin"] = new FileInfo(Path.Combine(outDir, "models.bin")).Length;
            report.SharedBytes["models.json"] = WriteJson(Path.Combine(outDir, "models.json"), w =>
            {
                w.WriteStartObject();
                w.WriteNumber("vertexBytes", 32);
                w.WriteNumber("vertexCount", vertexBytes / 32);
                w.WriteNumber("indexOffset", vertexBytes);
                w.WriteNumber("indexCount", indices.Count);
                w.WriteString("colour", "rgb565-in-padding");
                w.WriteString("notes",
                    "Vertex: f32 x3 position, i8 x4 normal x127 (4th byte: untextured face's alpha), f32 x2 uv, u16 texture " +
                    "((array << 12) | layer, 0xFFFF untextured), u16 untextured face's colour as RGB565, u32 zero. Indices " +
                    "follow the vertices (byte offset indexOffset), relative to each LOD's baseVertex. Positions are in world " +
                    "axes and units; swapping y and z mirrors the model, so triangle winding is reversed: draw both sides. " +
                    "lodDistances: the header's thresholds are squared distances in object units, written as sqrt(t) / 128 " +
                    "world units; null = no limit.");
                w.WriteStartObject("models");
                foreach (var a in json) a(w);
                w.WriteEndObject();
                w.WriteEndObject();
            });
        }

        static long WriteObjectsBin(string path, List<(MapObject O, int Index, int Model)> drawn)
        {
            var b = new byte[24];
            using (var f = File.Create(path))
                foreach (var (o, index, model) in drawn)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0), o.MapX / MapSpace.ObjectUnitsPerPoint);
                    BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), MapSpace.WorldYFromObject(o.Z));
                    BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), o.MapY / MapSpace.ObjectUnitsPerPoint);
                    var q = o.Rotation;
                    q = q.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(q);
                    // the y/z swap is a mirror: the rotation axis swaps too and the angle changes sign
                    var wq = new Quaternion(-q.X, -q.Z, -q.Y, q.W);
                    BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12), Q16(wq.X));
                    BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(14), Q16(wq.Y));
                    BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(16), Q16(wq.Z));
                    BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(18), Q16(wq.W));
                    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), (ushort)model);
                    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), (ushort)index);
                    f.Write(b);
                }
            return new FileInfo(path).Length;
        }

        static short Q16(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), -32767, 32767);

        static long WriteJson(string path, Action<Utf8JsonWriter> write)
        {
            using (var f = File.Create(path))
            using (var w = new Utf8JsonWriter(f, new JsonWriterOptions { Indented = false }))
                write(w);
            return new FileInfo(path).Length;
        }
    }
}
