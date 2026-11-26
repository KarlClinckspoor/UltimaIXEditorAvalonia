using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using U9.Formats.Flx;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9Editor.Core;

namespace U9Editor.Cli
{
    /// <summary>u9 kit: the pieces a map is built from and how they join (U9Editor.Core.KitCatalog).</summary>
    static class KitCommands
    {
        static readonly EnumerationOptions Eo = new() { MatchCasing = MatchCasing.CaseInsensitive };

        /// <summary>The kit of a map's fixed objects (or nonfixed with "nonfixed").</summary>
        public static KitCatalog Load(string gamedata, int map, bool nonfixed = false)
        {
            var game = GameFolder.TryOpen(gamedata) ?? throw new IOException("not a game folder: " + gamedata);
            string F(string folder, string name) => Directory.EnumerateFiles(folder, name, Eo).FirstOrDefault();
            string file = nonfixed ? F(Path.Combine(game.Root, "runtime"), "nonfixed." + map) : F(game.StaticFolder, "fixed." + map);
            if (file == null) throw new IOException($"map {map} has no {(nonfixed ? "nonfixed" : "fixed")} file");
            var objects = ObjectMap.Load(file, nonfixed ? ObjectFileKind.Nonfixed : ObjectFileKind.Fixed);
            var types = TypeTable.Load(F(game.StaticFolder, "types.dat"));
            string tn = F(game.StaticFolder, "typename.flx");
            var names = tn != null ? TypeNames.Load(tn) : null;
            using var sap = FlxArchive.Open(F(game.StaticFolder, "sappear.flx"));
            return KitCatalog.Build(map, objects.Objects, types, m => m < sap.Count ? sap.Read(m) : null, t => names?[t]);
        }

        /// <summary>The commonest turns modulo 90 (whole degrees) and how many upright pieces have each.</summary>
        static IEnumerable<string> TurnGroups(string gamedata, int map, bool nonfixed)
        {
            var game = GameFolder.TryOpen(gamedata);
            string file = nonfixed ? Directory.EnumerateFiles(Path.Combine(game.Root, "runtime"), "nonfixed." + map, Eo).First()
                                   : Directory.EnumerateFiles(game.StaticFolder, "fixed." + map, Eo).First();
            return ObjectMap.Load(file, nonfixed ? ObjectFileKind.Nonfixed : ObjectFileKind.Fixed).Objects
                .Where(o => !o.Removed).Select(o => KitCatalog.UprightYaw(o.Rotation)).Where(y => y != null)
                .GroupBy(y => { int a = (int)MathF.Round(((y.Value % 90) + 90) % 90) % 90; return a >= 45 ? a - 90 : a; })
                .OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key}°×{g.Count()}");
        }

        /// <summary>
        /// u9 wall gamedata map --kit N --types a,b --z Z [--flip] [--dry-run] x,y x,y ...: pieces of map N's kit strung
        /// along the points (object units), written to the map's fixed file (the original kept as .orig).
        /// </summary>
        public static int Wall(string gamedata, int map, string[] args) => Build(gamedata, map, args, "wall");

        /// <summary>u9 floor gamedata map --kit N --type T --z Z [--angle 90] [--dry-run] [--file F] x,y x,y x,y ...: tiles filling the polygon.</summary>
        public static int Floor(string gamedata, int map, string[] args) => Build(gamedata, map, args, "floor");

        /// <summary>u9 room gamedata map --kit N --types W --corner C --floor F --z Z [--flip] [--dry-run] [--file F] a b c: a rectangular room.</summary>
        public static int Room(string gamedata, int map, string[] args) => Build(gamedata, map, args, "room");

        static int Build(string gamedata, int map, string[] args, string mode)
        {
            string Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            bool floor = mode == "floor";
            int kitMap = int.Parse(Opt("--kit") ?? map.ToString(), CultureInfo.InvariantCulture);
            var pattern = (Opt(floor ? "--type" : "--types") ?? throw new ArgumentException(floor ? "--type T is needed" : "--types a,b,... is needed"))
                          .Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
            int z = int.Parse(Opt("--z") ?? throw new ArgumentException("--z is needed"), CultureInfo.InvariantCulture);
            float angle = float.Parse(Opt("--angle") ?? "0", CultureInfo.InvariantCulture);
            int corner = int.Parse(Opt("--corner") ?? "-1", CultureInfo.InvariantCulture);
            int floorType = int.Parse(Opt("--floor") ?? "-1", CultureInfo.InvariantCulture);
            string targetFile = Opt("--file");
            var valued = new HashSet<int>(new[] { "--kit", "--types", "--type", "--z", "--angle", "--corner", "--floor", "--file" }.Select(n => Array.IndexOf(args, n) + 1).Where(i => i > 0));
            var points = args.Where((a, i) => !a.StartsWith("--") && !valued.Contains(i)).Select(a =>
            {
                var p = a.Split(',');
                return new System.Numerics.Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
            }).ToList();
            int need = mode == "wall" ? 2 : 3;
            if (points.Count < need) throw new ArgumentException($"at least {need} points x,y are needed");
            var kit = Load(gamedata, kitMap);
            var plan = mode switch
            {
                "floor" => KitBuilding.Fill(kit, pattern[0], points, z, angle),
                "room" => KitBuilding.Room(kit, floorType, pattern, corner, points[0], points[1], points[2], z, args.Contains("--flip")),
                _ => KitBuilding.Wall(kit, pattern, points, z, args.Contains("--flip"), angle, corner),
            };
            foreach (var p in plan)
                Console.WriteLine(FormattableString.Invariant($"  {kit.Pieces[p.Type].Label,-24} at {p.MapX},{p.MapY},{p.Z} turned {p.Yaw:0.#}°"));
            if (args.Contains("--dry-run")) { Console.WriteLine($"{plan.Count} pieces (dry run: nothing written)"); return 0; }
            var game = GameFolder.TryOpen(gamedata);
            // --file: a fixed file elsewhere (a staged copy), written in place; else the game's, keeping .orig
            string file = targetFile ?? Directory.EnumerateFiles(game.StaticFolder, "fixed." + map, Eo).FirstOrDefault() ?? throw new IOException($"map {map} has no fixed file");
            string source = Directory.EnumerateFiles(game.StaticFolder, "fixed." + kitMap, Eo).First();
            var target = ObjectMap.Load(file, ObjectFileKind.Fixed);
            var from = kitMap == map && targetFile == null ? null : ObjectMap.Load(source, ObjectFileKind.Fixed);
            KitBuilding.Apply(target, from, plan, mode);
            if (targetFile == null && !File.Exists(file + ".orig")) File.Copy(file, file + ".orig");
            target.Save(file);
            Console.WriteLine($"{plan.Count} pieces written to {file}");
            return 0;
        }

        /// <summary>A 96-pixel picture of each piece's model as type.png, and all of them side by side as sheet.png.</summary>
        static void WriteThumbs(string gamedata, IEnumerable<KitPiece> pieces, string dir)
        {
            var game = GameFolder.TryOpen(gamedata);
            Directory.CreateDirectory(dir);
            using var sap = FlxArchive.Open(Directory.EnumerateFiles(game.StaticFolder, "sappear.flx", Eo).First());
            using var bmp = FlxArchive.Open(Directory.EnumerateFiles(game.StaticFolder, "bitmap16.flx", Eo).First());
            var cache = new Dictionary<(int, int), U9.Formats.Bitmaps.BitmapFrame>();
            const int S = 96;
            var shots = new List<byte[]>();
            foreach (var p in pieces)
            {
                var px = ModelThumbnail.Render(p.Model < sap.Count ? sap.Read(p.Model) : null, b => b < bmp.Count ? bmp.Read(b) : null, S, cache);
                if (px == null) continue;
                PngWriter.WriteRgba(Path.Combine(dir, $"{p.Type}.png"), S, S, px);
                shots.Add(px);
            }
            if (shots.Count == 0) return;
            int cols = Math.Min(8, shots.Count), rows = (shots.Count + cols - 1) / cols;
            var sheet = new byte[cols * S * rows * S * 4];
            for (int i = 0; i < shots.Count; i++)
                for (int y = 0; y < S; y++)
                    Buffer.BlockCopy(shots[i], y * S * 4, sheet, (((i / cols) * S + y) * cols * S + (i % cols) * S) * 4, S * 4);
            PngWriter.WriteRgba(Path.Combine(dir, "sheet.png"), cols * S, rows * S, sheet);
            Console.WriteLine($"{shots.Count} pictures in {dir} (sheet.png: all of them, {cols} a row)");
        }

        public static int Print(string gamedata, int map, string[] opts)
        {
            int ti = Array.IndexOf(opts, "--type");
            int only = ti >= 0 && ti + 1 < opts.Length ? int.Parse(opts[ti + 1], CultureInfo.InvariantCulture) : -1;
            int mi = Array.IndexOf(opts, "--max");
            int max = mi >= 0 && mi + 1 < opts.Length ? int.Parse(opts[mi + 1], CultureInfo.InvariantCulture) : 40;
            var kit = Load(gamedata, map, opts.Contains("nonfixed"));
            var pieces = kit.ByUse.Where(p => only < 0 || p.Type == only).ToList();
            int thi = Array.IndexOf(opts, "--thumbs");
            if (thi >= 0 && thi + 1 < opts.Length) WriteThumbs(gamedata, pieces.Take(max), opts[thi + 1]);
            Console.WriteLine($"map {map}: {kit.Pieces.Values.Sum(p => p.Count)} pieces of {kit.Pieces.Count} types, {kit.Joins.Count} joins");
            var turns = kit.Pieces.Count == 0 ? "" : string.Join(", ", TurnGroups(gamedata, map, opts.Contains("nonfixed")));
            Console.WriteLine($"joins imply a {kit.JoinStep}-unit grid; pieces by turn (modulo 90): {turns}");
            if (opts.Contains("--grid-only")) return 0;
            foreach (var p in pieces.Take(only >= 0 ? 1 : max))
            {
                var s = p.Size;
                Console.WriteLine(FormattableString.Invariant($"{p.Label,-32} x{p.Count,-4} model {p.Model,5}  size {s.X:0}×{s.Y:0}×{s.Z:0}  box x {p.BoxMin.X:0}..{p.BoxMax.X:0} y {p.BoxMin.Y:0}..{p.BoxMax.Y:0} z {p.BoxMin.Z:0}..{p.BoxMax.Z:0}"));
                foreach (var j in p.Joins.Take(only >= 0 ? 30 : 4))
                {
                    string to = kit.Pieces.TryGetValue(j.To, out var q) ? q.Label : "type " + j.To;
                    Console.WriteLine(FormattableString.Invariant($"      -> {to,-28} at ({j.Offset.X:0}, {j.Offset.Y:0}, {j.Offset.Z:0}) turned {j.Turn,3}°  x{j.Count}"));
                }
            }
            if (pieces.Count > max && only < 0) Console.WriteLine($"... {pieces.Count - max} more types (--max)");
            return 0;
        }
    }
}
