using U9Editor.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Flx;
using U9.Formats.Terrain;

namespace U9Editor.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0) return Usage();
            try
            {
                switch (args[0])
                {
                    case "flx-info" when args.Length == 2: return FlxInfo(args[1]);
                    case "flx-unpack" when args.Length == 3: return FlxUnpack(args[1], args[2]);
                    case "terrain-info" when args.Length == 2: return TerrainInfo(args[1]);
                    case "terrain-heightmap" when args.Length == 3: return TerrainHeightmap(args[1], args[2]);
                    case "terrain-textures" when args.Length == 3: return TerrainTextures(args[1], args[2]);
                    case "bitmap-frames" when args.Length == 4: return BitmapFrames(args[1], int.Parse(args[2]), args[3]);
                    case "export-web" when args.Length >= 3: return ExportWeb(args);
                    default: return Usage();
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        static int Usage()
        {
            Console.Error.WriteLine(@"usage:
  u9 flx-info <file.flx>                     list the records of an FLX archive
  u9 flx-unpack <file.flx> <out-dir>         write each record to <out-dir>/NNNNN.bin
  u9 terrain-info <terrain.N>                header, tile and chunk summary
  u9 terrain-heightmap <terrain.N> <out.png> grayscale height picture (point row 0 at the top, 1 pixel per point)
  u9 terrain-textures <terrain.N> <out.png>  false-colour picture of the texture numbers
  u9 bitmap-frames <bitmap16.flx> <record> <out.png>  every frame of a bitmap record, side by side (row 0 at the top)
  u9 export-web <gamedata> <out> [--maps 9,10,...] [--texture-max 128] [--no-models]
                                             bake maps for the U9 web viewer (UltimaCartographer/u9viewer/EXPORT.md)");
            return 2;
        }

        static int FlxInfo(string path)
        {
            using var a = FlxArchive.Open(path);
            var used = a.UsedIndices().ToList();
            long total = used.Sum(i => (long)a.LengthOf(i));
            Console.WriteLine($"{Path.GetFileName(path)}: {a.Count} slots, {used.Count} used, {total} bytes of records, version {a.Version}");
            foreach (int i in used) Console.WriteLine($"{i,6} {a.LengthOf(i),10}");
            return 0;
        }

        static int FlxUnpack(string path, string outDir)
        {
            Directory.CreateDirectory(outDir);
            using var a = FlxArchive.Open(path);
            int n = 0;
            foreach (int i in a.UsedIndices())
            {
                File.WriteAllBytes(Path.Combine(outDir, $"{i:D5}.bin"), a.Read(i));
                n++;
            }
            Console.WriteLine($"wrote {n} records to {outDir}");
            return 0;
        }

        static int TerrainInfo(string path)
        {
            var t = TerrainMap.Load(path);
            Console.WriteLine($"name:        {t.Name}");
            Console.WriteLine($"size:        {t.Width} x {t.Height} points, {t.TilesX} x {t.TilesY} tiles");
            Console.WriteLine($"water level: {t.WaterLevel} (point height {t.WaterHeight})");
            Console.WriteLine($"waves:       {t.WaveAmplitude}");
            Console.WriteLine($"flags:       0x{t.Flags:X}");
            Console.WriteLine($"chunks:      {t.Chunks.Count} ({t.TileChunks.Distinct().Count()} used by tiles)" +
                              (t.DeclaredChunkCount.HasValue ? $", header says {t.DeclaredChunkCount}" : ""));
            if (t.TrailingData.Length > 0) Console.WriteLine($"trailing:    {t.TrailingData.Length} bytes after the last chunk");
            if (t.Width > 0)
            {
                int min = int.MaxValue, max = int.MinValue, holes = 0;
                var textures = new Dictionary<int, int>();
                for (int y = 0; y < t.Height; y++)
                    for (int x = 0; x < t.Width; x++)
                    {
                        var p = t.GetPoint(x, y);
                        min = Math.Min(min, p.Height); max = Math.Max(max, p.Height);
                        if (p.Hole) holes++;
                        textures[p.Texture] = textures.TryGetValue(p.Texture, out int c) ? c + 1 : 1;
                    }
                Console.WriteLine($"heights:     {min}..{max}, {holes} hole points, {textures.Count} textures used");
                Console.WriteLine("top textures: " + string.Join(", ", textures.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key}x{kv.Value}")));
            }
            return 0;
        }

        static int TerrainHeightmap(string path, string outPng)
        {
            var t = TerrainMap.Load(path);
            if (t.Width == 0) { Console.Error.WriteLine("map has no terrain"); return 1; }
            int min = int.MaxValue, max = int.MinValue;
            for (int y = 0; y < t.Height; y++)
                for (int x = 0; x < t.Width; x++) { int h = t.GetPoint(x, y).Height; min = Math.Min(min, h); max = Math.Max(max, h); }
            var px = new byte[t.Width * t.Height];
            float range = Math.Max(1, max - min);
            for (int y = 0; y < t.Height; y++)
                for (int x = 0; x < t.Width; x++)
                    px[x + y * t.Width] = (byte)((t.GetPoint(x, y).Height - min) * 255 / range);
            PngWriter.WriteGray(outPng, t.Width, t.Height, px);
            Console.WriteLine($"wrote {outPng} ({t.Width}x{t.Height}, heights {min}..{max})");
            return 0;
        }

        static int ExportWeb(string[] args)
        {
            var game = GameFolder.TryOpen(args[1]);
            if (game == null) { Console.Error.WriteLine($"{args[1]} is not an Ultima IX folder (no static/terrain.*)"); return 1; }
            var o = new WebExportOptions();
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--maps" when i + 1 < args.Length: o.Maps = args[++i].Split(',').Select(int.Parse).ToArray(); break;
                    case "--texture-max" when i + 1 < args.Length: o.TextureMax = int.Parse(args[++i]); break;
                    case "--no-models": o.Models = false; break;
                    default: return Usage();
                }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = WebExport.Run(game, args[2], o, Console.WriteLine);
            Console.WriteLine();
            Console.Write(report);
            Console.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s");
            return report.Problems.Count == 0 ? 0 : 3;
        }

        static int BitmapFrames(string flx, int record, string outPng)
        {
            using var a = U9.Formats.Flx.FlxArchive.Open(flx);
            var rec = a.Read(record);
            int n = U9.Formats.Bitmaps.Bitmap16.FrameCount(rec);
            var frames = new List<U9.Formats.Bitmaps.BitmapFrame>();
            for (int i = 0; i < n; i++) frames.Add(U9.Formats.Bitmaps.Bitmap16.ReadFrame(rec, i));
            int gap = 4, w = 0, h = 0;
            foreach (var f in frames) { w += f.Width + gap; h = Math.Max(h, f.Height); }
            var px = new byte[w * h * 3];
            int x0 = 0;
            foreach (var f in frames)
            {
                for (int y = 0; y < f.Height; y++)
                    for (int x = 0; x < f.Width; x++)
                        for (int c = 0; c < 3; c++) px[(x0 + x + y * w) * 3 + c] = f.Rgba[(x + y * f.Width) * 4 + c];
                x0 += f.Width + gap;
            }
            PngWriter.WriteRgb(outPng, w, h, px);
            Console.WriteLine($"wrote {outPng}: {n} frames");
            return 0;
        }

        static int TerrainTextures(string path, string outPng)
        {
            var t = TerrainMap.Load(path);
            if (t.Width == 0) { Console.Error.WriteLine("map has no terrain"); return 1; }
            var px = new byte[t.Width * t.Height * 3];
            for (int y = 0; y < t.Height; y++)
                for (int x = 0; x < t.Width; x++)
                {
                    var p = t.GetPoint(x, y);
                    uint h = (uint)p.Texture * 2654435761u;
                    int shade = 128 + p.Height * 127 / TerrainPoint.MaxHeight;
                    int o = (x + y * t.Width) * 3;
                    px[o] = (byte)(((h >> 24) & 0xFF) * shade / 255);
                    px[o + 1] = (byte)(((h >> 16) & 0xFF) * shade / 255);
                    px[o + 2] = (byte)(((h >> 8) & 0xFF) * shade / 255);
                    if (p.Hole) { px[o] = 255; px[o + 1] = 0; px[o + 2] = 255; }
                }
            PngWriter.WriteRgb(outPng, t.Width, t.Height, px);
            Console.WriteLine($"wrote {outPng}");
            return 0;
        }
    }
}
