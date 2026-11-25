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
                    case "model-export" when args.Length >= 4: return ModelExport(args);
                    case "model-info" when args.Length == 2: return ModelInfo(args[1]);
                    case "model-import" when args.Length == 4: return ModelImport(args[1], int.Parse(args[2]), args[3]);
                    case "orbit-scene" when args.Length == 12 || args.Length == 13: return OrbitScene(args);
                    case "prefab-copy" when args.Length >= 8: return PrefabCopy(args);
                    case "prefab-paste" when args.Length >= 6: return PrefabPaste(args);
                    case "camera-point" when args.Length >= 9: return CameraPointSet(args);
                    case "scenes" when args.Length == 3: return Scenes(args[1], int.Parse(args[2]));
                    case "script" when args.Length == 3: return ScriptPrint(args[1], int.Parse(args[2]));
                    case "script-uses" when args.Length >= 3: return ScriptUses.Run(args[1], args[2], args.Skip(3).ToArray());
                    case "script-compile" when args.Length == 4: return ScriptCompile(args[1], int.Parse(args[2]), args[3]);
                    case "objects" when args.Length >= 3: return Objects(args[1], int.Parse(args[2]), args.Skip(3).ToArray());
                    case "speech-wav" when args.Length == 4: return SpeechWav(args[1], int.Parse(args[2]), args[3]);
                    case "line" when args.Length == 3: return LinePrint(args[1], int.Parse(args[2]));
                    case "combat" when args.Length == 2: return CombatCommands.Print(args[1]);
                    case "combat-set" when args.Length >= 3: return CombatCommands.Set(args[1], args.Skip(2));
                    case "combat-exe" when args.Length == 3: return CombatCommands.WriteExe(args[1], args[2]);
                    case "combat-balance" when args.Length >= 2: return CombatCommands.Balance(args[1], args.Skip(2).ToArray());
                    default: return Usage();
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        /// <summary>A conversation line's voice clip (sound/Speech.flx, EA MicroTalk) as a 16-bit WAV.</summary>
        static int SpeechWav(string gamedata, int line, string output)
        {
            string path = U9.Mods.GamePaths.Find(gamedata, "sound/speech.flx") ?? throw new IOException("no sound/Speech.flx in " + gamedata);
            var rec = U9.Formats.Text.SpeechTable.ReadRecord(path, line) ?? throw new IOException($"line {line} has no voice clip");
            var clip = U9.Formats.Text.SpeechClip.Parse(line, rec, rec.Length).Value;
            if (clip.Codec != 2) throw new InvalidDataException($"clip codec {clip.Codec}: only 2 (EA MicroTalk) is decoded");
            var samples = U9.Formats.Text.MicroTalk.Decode(rec, U9.Formats.Text.SpeechClip.HeaderSize);
            File.WriteAllBytes(output, U9.Formats.Text.MicroTalk.Wav(samples, clip.Rate));
            Console.WriteLine($"{clip.Name}: {samples.Length} samples, {samples.Length / (double)clip.Rate:0.00} s -> {output}");
            return 0;
        }

        /// <summary>A text.flx line with its clip.</summary>
        static int LinePrint(string gamedata, int line)
        {
            var t = U9.Formats.Text.TextTable.Load(U9.Mods.GamePaths.Find(gamedata, "static/text.flx") ?? throw new IOException("no static/text.flx"));
            Console.WriteLine($"{line}: {t[line]}");
            return 0;
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
                                             bake maps for the U9 web viewer (UltimaCartographer/u9viewer/EXPORT.md)
  u9 model-export <gamedata> <sappear record> <out.glb> [--lod n]
                                             a model as binary glTF for Blender (textures embedded; x east, y north, z up)
  u9 model-info <file.glb>                   what the editor reads from a .glb: triangles per texture, size
  u9 model-import <gamedata> <sappear record> <in.glb>
                                             replace a model with a .glb's triangles (static/sappear.flx; the first
                                             time the original is kept as sappear.flx.orig)
  u9 objects <gamedata> <map> [fixed|nonfixed] [--type n] [--props]
                                             list a map's objects: file, type, name, point x / row, z, link, model,
                                             and with --props their property segments
  u9 scenes <gamedata> <map>                 the map's in-engine scenes: camera points (type 2218) and the moves
                                             the trigger scripts chain between them
  u9 combat <gamedata>                       u9.exe's combat tables (weapons, armour, natural attacks, damage
                                             multipliers) with the pending patches, and those patches
  u9 combat-set <gamedata> <what:which.field=value>...
                                             change combat stats and save: weapon:TYPE.min|max|reach|radius,
                                             armour:TYPE.min|max, natural:ROW.min|max|types, mult:KIND.fire (or a bit
                                             number), npc:RECORD.hp|defence|kind|mana. u9.exe changes go to
                                             u9.exe.patches.json (never into u9.exe); NPC.FLX keeps NPC.FLX.orig
  u9 combat-exe <gamedata> <out.exe>         u9.exe with the pending patches, written elsewhere
  u9 combat-balance <gamedata> [--weapon TYPE] [--armour 0-6] [--str 0-3] [--hp N] [--shield TYPE]
                                             mean hits for the Avatar to kill each fighter, and for each to kill
                                             the Avatar
  u9 script <gamedata> <slot>               a trigger script in the trigger language (docs/trigger-language.md)
  u9 script-uses <gamedata> <op> [--map N] [--max N]
                                             the scripts that use a command (op in hex, e.g. 3E), shortest first,
                                             with the objects that run them: map, file offset, type, position,
                                             link and which of their four scripts it is, and the tp and run
                                             commands that go there and run it with tools/u9mon.py
  u9 script-compile <gamedata> <slot> <file> compile the file into that script slot and write static/TRIGGERS.FLX
                                             (first time keeping .orig); errors and warnings go to stderr
  u9 camera-point <gamedata> <map> <link> <x> <row> <z> <heading> <pitch> [distance]
                                             put a camera point (type 2218, by link) where a camera should be: the
                                             point looked at (point x, row; z in object units), heading clockwise
                                             from north, pitch down, distance in object units; writes
                                             runtime/nonfixed.N (first time keeping .orig)
  u9 prefab-copy <gamedata> <map> <x0> <row0> <x1> <row1> <out.u9prefab> [--margin n] [--no-ground]
                                             every object in that rectangle of points and the
                                             ground under it (n points round, default 3) as a
                                             prefab file
  u9 prefab-paste <gamedata> <map> <x> <row> <in.u9prefab> [--turn k] [--ground none|heights|textures]
                  [--keep-heights] [--no-blend] [--keep-links] [--share-scripts] [--clear] [--together]
                                             paste it with its first point at (x, row), k
                                             quarter turns counter-clockwise; writes the map's
                                             files and TRIGGERS.FLX (first time keeping .orig)
  u9 orbit-scene <gamedata> <map> <x> <row> <z> <radius> <height> <points> <seconds> <zone x> <zone row> [<zone z>]
                                             a scene circling the point (x, row; z object units) at radius points
                                             and height units above it, started by a new trigger zone; writes
                                             runtime/nonfixed.N and static/TRIGGERS.FLX (first time keeping .orig)");
            return 2;
        }

        static int ModelExport(string[] args)
        {
            var g = GameFolder.TryOpen(args[1]) ?? throw new IOException($"{args[1]} is not an Ultima IX folder");
            int record = int.Parse(args[2]);
            int lod = 0;
            for (int i = 4; i < args.Length; i++) if (args[i] == "--lod" && i + 1 < args.Length) lod = int.Parse(args[++i]);
            using var sap = FlxArchive.Open(Path.Combine(g.StaticFolder, "sappear.flx"));
            using var bmp = FlxArchive.Open(Path.Combine(g.StaticFolder, "bitmap16.flx"));
            var r = sap.Read(record);
            if (!U9.Formats.Models.SappearModel.IsModel(r)) throw new InvalidDataException($"sappear record {record} is not a model");
            var glb = GltfModel.Export(r, t => t >= 0 && t < bmp.Count && bmp.Has(t) ? bmp.Read(t) : null, lod, $"sappear {record}");
            File.WriteAllBytes(args[3], glb);
            Console.WriteLine($"{args[3]}: {glb.Length} bytes, {U9.Formats.Models.SappearModel.Triangles(r, lod).Count} triangles");
            return 0;
        }

        static int ModelImport(string gamedata, int record, string glbPath)
        {
            var g = GameFolder.TryOpen(gamedata) ?? throw new IOException($"{gamedata} is not an Ultima IX folder");
            string path = Path.Combine(g.StaticFolder, "sappear.flx");
            var tris = GltfModel.Import(File.ReadAllBytes(glbPath));
            if (tris.Count == 0) throw new InvalidDataException($"{glbPath} has no triangles");
            byte[] built;
            using (var sap = FlxArchive.Open(path))
            {
                var template = record < sap.Count && sap.Has(record) ? sap.Read(record) : null;
                built = U9.Formats.Models.SappearWriter.Build(tris, template);
                if (record >= sap.Count) sap.Resize(record + 1);
                sap.Write(record, built);
                if (!File.Exists(path + ".orig")) File.Copy(path, path + ".orig");
                sap.Save(path + ".new");
            }
            File.Move(path + ".new", path, true);
            Console.WriteLine($"sappear record {record}: {tris.Count} triangles, {built.Length} bytes (original kept as sappear.flx.orig)");
            return 0;
        }

        static int ModelInfo(string path)
        {
            var tris = GltfModel.Import(File.ReadAllBytes(path));
            Console.WriteLine($"{path}: {tris.Count} triangles");
            if (tris.Count == 0) return 0;
            var all = tris.SelectMany(t => new[] { t.P0, t.P1, t.P2 }).ToList();
            Console.WriteLine($"  box ({all.Min(p => p.X):0.#}, {all.Min(p => p.Y):0.#}, {all.Min(p => p.Z):0.#}) to ({all.Max(p => p.X):0.#}, {all.Max(p => p.Y):0.#}, {all.Max(p => p.Z):0.#}) object units");
            foreach (var grp in tris.GroupBy(t => (t.Texture, t.Frame, t.Flags)).OrderBy(x => x.Key.Texture))
                Console.WriteLine($"  texture {grp.Key.Texture} frame {grp.Key.Frame} flags 0x{grp.Key.Flags:X}: {grp.Count()} triangles");
            return 0;
        }

        static int Objects(string gamedata, int map, string[] opts)
        {
            var game = GameFolder.TryOpen(gamedata) ?? throw new IOException("not a game folder: " + gamedata);
            bool props = opts.Contains("--props");
            int type = -1;
            int ti = Array.IndexOf(opts, "--type");
            if (ti >= 0 && ti + 1 < opts.Length) type = int.Parse(opts[ti + 1]);
            var kinds = new List<U9.Formats.Objects.ObjectFileKind>();
            if (!opts.Contains("nonfixed")) kinds.Add(U9.Formats.Objects.ObjectFileKind.Fixed);
            if (!opts.Contains("fixed")) kinds.Add(U9.Formats.Objects.ObjectFileKind.Nonfixed);
            string typenames = Directory.EnumerateFiles(game.StaticFolder, "typename.flx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            var names = typenames != null ? U9.Formats.Objects.TypeNames.Load(typenames) : null;
            foreach (var kind in kinds)
            {
                bool fixd = kind == U9.Formats.Objects.ObjectFileKind.Fixed;
                string folder = fixd ? game.StaticFolder : Path.Combine(game.Root, "runtime");
                string file = Directory.EnumerateFiles(folder, (fixd ? "fixed." : "nonfixed.") + map, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
                if (file == null) continue;
                var m = U9.Formats.Objects.ObjectMap.Load(file, kind);
                foreach (var o in m.Objects)
                {
                    if (o.Removed || (type >= 0 && o.Type != type)) continue;
                    string line = $"{(fixd ? "fixed" : "nonfixed")} @{o.FileOffset:X6} type {o.Type,5} at {o.PointX,8:F2} {o.PointRow,8:F2} z {o.Z,5}";
                    if (!fixd) line += $" link {o.Link,5} model {o.Model,5}";
                    var q = o.Rotation;
                    if (q.W < 0.99999f) line += $" rot ({q.X:0.###}, {q.Y:0.###}, {q.Z:0.###}, {q.W:0.###})";
                    string n = names?[o.Type];
                    if (n != null) line += "  " + n;
                    Console.WriteLine(line);
                    if (props && !fixd && o.PropertyOffset != 0)
                        foreach (var p in m.PropertiesOf(o))
                            Console.WriteLine($"      {p}");
                }
            }
            return 0;
        }

        /// <summary>The trigger language's names: the game's, the shipped names file, the editor user's names file.</summary>
        internal static (U9.Formats.Triggers.TriggerScripts Scripts, U9.Formats.Triggers.Language.TriggerNames Names) ScriptSetup(string gamedata)
        {
            var game = GameFolder.TryOpen(gamedata) ?? throw new IOException("not a game folder: " + gamedata);
            var opt = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            string F(string n) => Directory.EnumerateFiles(game.StaticFolder, n, opt).FirstOrDefault() ?? throw new IOException($"no {n} in {game.StaticFolder}");
            var names = U9.Formats.Triggers.Language.TriggerNames.Build(
                U9.Formats.Objects.TypeNames.Load(F("typename.flx")), U9.Formats.Models.TypeTable.Load(F("types.dat")),
                Path.Combine(AppContext.BaseDirectory, "names", "trigger-names.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "U9Editor", "trigger-names.txt"));
            foreach (var p in names.Problems) Console.Error.WriteLine("names: " + p);
            return (U9.Formats.Triggers.TriggerScripts.Load(F("triggers.flx")), names);
        }

        static int ScriptPrint(string gamedata, int slot)
        {
            var (scripts, names) = ScriptSetup(gamedata);
            if (!scripts.IsUsed(slot)) { Console.Error.WriteLine($"script {slot} is not used"); return 1; }
            Console.Write(U9.Formats.Triggers.Language.TriggerPrinter.Print(scripts[slot], names, slot));
            return 0;
        }

        static int ScriptCompile(string gamedata, int slot, string file)
        {
            var (scripts, names) = ScriptSetup(gamedata);
            var r = U9.Formats.Triggers.Language.TriggerCompiler.Compile(File.ReadAllText(file), names, slot);
            foreach (var d in r.Diagnostics) Console.Error.WriteLine($"{file}:{d}");
            if (!r.Ok) return 1;
            scripts.Set(slot, r.Ops);
            string backup = scripts.Path + ".orig";
            if (!File.Exists(backup)) File.Copy(scripts.Path, backup);
            scripts.Save();
            Console.WriteLine($"script {slot}: {r.Ops.Length - 1} ops written to {scripts.Path}");
            return 0;
        }

        static int OrbitScene(string[] a)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var game = GameFolder.TryOpen(a[1]) ?? throw new IOException("not a game folder: " + a[1]);
            int map = int.Parse(a[2]);
            float x = float.Parse(a[3], ci), row = float.Parse(a[4], ci), radius = float.Parse(a[6], ci), seconds = float.Parse(a[9], ci);
            int z = int.Parse(a[5]), height = int.Parse(a[7]), count = int.Parse(a[8]);
            int zx = (int)Math.Round(double.Parse(a[10], ci) * 128), zy = (int)Math.Round(double.Parse(a[11], ci) * 128);
            var opt = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            string fxPath = Directory.EnumerateFiles(game.StaticFolder, "fixed." + map, opt).First();
            string nfPath = Directory.EnumerateFiles(Path.Combine(game.Root, "runtime"), "nonfixed." + map, opt).First();
            var t = U9.Formats.Terrain.TerrainMap.Load(Directory.EnumerateFiles(game.StaticFolder, "terrain." + map, opt).First());
            var space = new MapSpace(t.Width, t.Height);
            var fx = U9.Formats.Objects.ObjectMap.Load(fxPath, U9.Formats.Objects.ObjectFileKind.Fixed);
            var nf = U9.Formats.Objects.ObjectMap.Load(nfPath, U9.Formats.Objects.ObjectFileKind.Nonfixed);
            var ed = new MapEditor(map, fx, nf, SceneObjects.Build(fx, nf, space, null), space, new TerrainRaster(t)) { Terrain = t };
            ed.Scripts = U9.Formats.Triggers.TriggerScripts.Load(Directory.EnumerateFiles(game.StaticFolder, "triggers.flx", opt).First());
            // a trigger zone to copy: a map change zone of this map or of Britannia
            bool ok(U9.Formats.Objects.MapObject o) => o.Type == 1111 && o.Map.PropertiesOf(o).Any(p => p.Id == U9.Formats.Objects.PropertySegment.OoTrigger)
                                                       && o.Map.PropertiesOf(o).Any(p => p.Id == U9.Formats.Objects.PropertySegment.TriggerArea);
            var zone = nf.Objects.FirstOrDefault(ok) ?? throw new IOException("no trigger zone on this map to copy");
            var seg = nf.Data.AsSpan(nf.DataStart + zone.PropertyOffset, 16).ToArray();
            int gz = a.Length > 12 ? int.Parse(a[12]) : ed.GroundZ(zx, zy);     // default: on the terrain
            var made = ed.NewOrbitScene(zone.Raw, seg, zx, zy, gz, x, row, z, radius, height, count, seconds);
            var scene = Cutscenes.Find(ed.Nonfixed, ed.Scripts, space).First(p => p.Starter == made.Source);
            Console.WriteLine($"zone at ({zx / 128.0:0.##}, {zy / 128.0:0.##}) z {gz}: {scene}");
            foreach (var s in scene.Shots) Console.WriteLine($"   {s}   {s.Point.Pose}");
            foreach (var w in ed.Save(fxPath, nfPath)) Console.WriteLine("wrote " + w);
            return 0;
        }

        /// <summary>A map's editor from the game folder, for the commands that edit and save: editor, fixed and nonfixed paths.</summary>
        static (MapEditor Editor, string Fixed, string Nonfixed) OpenMap(GameFolder game, int map)
        {
            var opt = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            string fxPath = Directory.EnumerateFiles(game.StaticFolder, "fixed." + map, opt).FirstOrDefault();
            string nfPath = Directory.EnumerateFiles(Path.Combine(game.Root, "runtime"), "nonfixed." + map, opt).FirstOrDefault();
            string tPath = Directory.EnumerateFiles(game.StaticFolder, "terrain." + map, opt).FirstOrDefault() ?? throw new IOException($"map {map} has no terrain file");
            var t = U9.Formats.Terrain.TerrainMap.Load(tPath);
            var space = new MapSpace(t.Width, t.Height);
            var fx = fxPath != null ? U9.Formats.Objects.ObjectMap.Load(fxPath, U9.Formats.Objects.ObjectFileKind.Fixed) : null;
            var nf = nfPath != null ? U9.Formats.Objects.ObjectMap.Load(nfPath, U9.Formats.Objects.ObjectFileKind.Nonfixed) : null;
            string namesPath = Directory.EnumerateFiles(game.StaticFolder, "typename.flx", opt).FirstOrDefault();
            var names = namesPath != null ? U9.Formats.Objects.TypeNames.Load(namesPath) : null;
            var ed = new MapEditor(map, fx, nf, SceneObjects.Build(fx, nf, space, names), space, new TerrainRaster(t)) { Terrain = t, TerrainPath = tPath };
            string trig = Directory.EnumerateFiles(game.StaticFolder, "triggers.flx", opt).FirstOrDefault();
            if (trig != null) ed.Scripts = U9.Formats.Triggers.TriggerScripts.Load(trig);
            return (ed, fxPath, nfPath);
        }

        static int PrefabCopy(string[] a)
        {
            var game = GameFolder.TryOpen(a[1]) ?? throw new IOException("not a game folder: " + a[1]);
            int map = int.Parse(a[2]), x0 = int.Parse(a[3]), y0 = int.Parse(a[4]), x1 = int.Parse(a[5]), y1 = int.Parse(a[6]);
            int margin = 3; bool ground = true;
            for (int i = 8; i < a.Length; i++)
            {
                if (a[i] == "--margin" && i + 1 < a.Length) margin = int.Parse(a[++i]);
                else if (a[i] == "--no-ground") ground = false;
                else throw new ArgumentException("unknown option " + a[i]);
            }
            var (ed, _, _) = OpenMap(game, map);
            var inside = ed.Objects.All.Where(o => o.Source.MapX >= x0 * 128 && o.Source.MapX <= x1 * 128 && o.Source.MapY >= y0 * 128 && o.Source.MapY <= y1 * 128).ToList();
            var p = Prefab.Copy(ed, inside, margin, ground);
            p.Save(a[7]);
            Console.WriteLine($"{p.Name}: {p.Describe()} -> {a[7]}");
            return 0;
        }

        static int PrefabPaste(string[] a)
        {
            var game = GameFolder.TryOpen(a[1]) ?? throw new IOException("not a game folder: " + a[1]);
            int map = int.Parse(a[2]), x = int.Parse(a[3]), row = int.Parse(a[4]), turns = 0;
            var opt = new PasteOptions();
            for (int i = 6; i < a.Length; i++)
            {
                switch (a[i])
                {
                    case "--turn" when i + 1 < a.Length: turns = int.Parse(a[++i]); break;
                    case "--ground" when i + 1 < a.Length:
                        opt.Ground = a[++i] switch { "none" => PrefabGround.None, "heights" => PrefabGround.Heights, "textures" => PrefabGround.HeightsAndTextures, var v => throw new ArgumentException("--ground none|heights|textures, not " + v) };
                        break;
                    case "--keep-heights": opt.FitHeights = false; break;
                    case "--no-blend": opt.BlendEdge = false; break;
                    case "--keep-links": opt.NewLinks = false; break;
                    case "--share-scripts": opt.CopyScripts = false; break;
                    case "--clear": opt.ClearArea = true; break;
                    case "--together": opt.ObjectsFollowGround = false; break;
                    default: throw new ArgumentException("unknown option " + a[i]);
                }
            }
            var p = Prefab.Load(a[5]).Turned(turns);
            var (ed, fxPath, nfPath) = OpenMap(game, map);
            var r = ed.PastePrefab(p, x, row, opt);
            Console.WriteLine($"pasted {r.Objects.Count} objects of {p.Name} at ({x}, {row}), turned {turns * 90}°" +
                              (r.GroundPasted ? $", ground fitted by {r.HeightOffset}" : "") + (r.LinkShift != 0 ? $", links +{r.LinkShift}" : "") +
                              (r.Slots.Count > 0 ? ", scripts " + string.Join(", ", r.Slots.Select(kv => $"{kv.Key}->{kv.Value}")) : "") +
                              (r.Skipped > 0 ? $", {r.Skipped} left out" : "") + (r.Cleared > 0 ? $", {r.Cleared} cleared" : ""));
            foreach (var n in r.Notes) Console.WriteLine("  " + n);
            foreach (var w in ed.Save(fxPath, nfPath)) Console.WriteLine("wrote " + w);
            return 0;
        }

        static int CameraPointSet(string[] a)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var game = GameFolder.TryOpen(a[1]) ?? throw new IOException("not a game folder: " + a[1]);
            int map = int.Parse(a[2]), link = int.Parse(a[3]);
            double x = double.Parse(a[4], ci), row = double.Parse(a[5], ci);
            int z = int.Parse(a[6]);
            float heading = float.Parse(a[7], ci), pitch = float.Parse(a[8], ci);
            int distance = a.Length > 9 ? int.Parse(a[9]) : -1;
            string file = Directory.EnumerateFiles(Path.Combine(game.Root, "runtime"), "nonfixed." + map, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault()
                          ?? throw new IOException($"map {map} has no nonfixed file");
            var m = U9.Formats.Objects.ObjectMap.Load(file, U9.Formats.Objects.ObjectFileKind.Nonfixed);
            var points = m.Objects.Where(o => !o.Removed && o.Type == CameraPoint.Type && o.Link == link).ToList();
            if (points.Count == 0) { Console.Error.WriteLine($"no camera point with link {link} on map {map}"); return 1; }
            foreach (var o in points)
            {
                o.MoveTo((int)Math.Round(x * 128), (int)Math.Round(row * 128), z + CameraPoint.PivotDrop);
                o.SetRotation(CameraPoint.RotationFor(heading, pitch));
                var d = m.PropertiesOf(o).FirstOrDefault(p => p.Id == U9.Formats.Objects.PropertySegment.MiscUsecode1);
                if (distance >= 0 && d != null) m.SetProperty(o, d, distance, d.B);
                Console.WriteLine($"camera point {link}: {new CameraPoint(o, new MapSpace(1024, 1024)).Pose}");
            }
            if (!File.Exists(file + ".orig")) File.Copy(file, file + ".orig");
            m.Save(file);
            return 0;
        }

        static int Scenes(string gamedata, int map)
        {
            var game = GameFolder.TryOpen(gamedata) ?? throw new IOException("not a game folder: " + gamedata);
            var opt = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            string file = Directory.EnumerateFiles(Path.Combine(game.Root, "runtime"), "nonfixed." + map, opt).FirstOrDefault()
                          ?? throw new IOException($"map {map} has no nonfixed file");
            var nonfixed = U9.Formats.Objects.ObjectMap.Load(file, U9.Formats.Objects.ObjectFileKind.Nonfixed);
            var scripts = U9.Formats.Triggers.TriggerScripts.Load(Directory.EnumerateFiles(game.StaticFolder, "triggers.flx", opt).First());
            var entry = game.Maps().FirstOrDefault(m => m.Number == map);
            var space = new MapSpace(entry?.Width ?? 1024, entry?.Height ?? 1024);
            var paths = Cutscenes.Find(nonfixed, scripts, space);
            Console.WriteLine($"map {map}: {nonfixed.Objects.Count(o => o.Type == CameraPoint.Type)} camera points, {paths.Count} scenes");
            foreach (var p in paths)
            {
                Console.WriteLine(p);
                foreach (var s in p.Shots)
                    Console.WriteLine($"   {s}   {s.Point.Pose}   (script {s.Slot} op {s.OpIndex}{(s.Also.Length > 0 ? "; also " + s.Also : "")})");
            }
            return 0;
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
