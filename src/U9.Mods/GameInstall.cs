using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace U9.Mods
{
    /// <summary>
    /// One game folder under the mod manager (docs/mod-format.md, "The manager"): applies a set of mods all or nothing,
    /// keeps the originals in a vault, puts the original game back, and writes the patched executable as u9-mod.exe.
    /// </summary>
    public sealed class GameInstall
    {
        public const string MarkerName = ".u9mm-target.json", NewSuffix = ".u9mm-new";
        public string Root { get; }
        public string Home { get; }
        public string ExePath { get; }
        /// <summary>The original u9.exe's identity (the vault is per executable).</summary>
        public FileId ExeId { get; }
        string Vault => Path.Combine(Home, "vault", ExeId.Sha256[..16]);
        string Journal => Path.Combine(Home, "journal-" + ExeId.Sha256[..16] + ".json");
        string Marker => Path.Combine(Root, MarkerName);

        /// <summary>The manager's own folder: U9MM_HOME, else %APPDATA%\U9ModManager.</summary>
        public static string DefaultHome =>
            Environment.GetEnvironmentVariable("U9MM_HOME") is { Length: > 0 } h ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "U9ModManager");

        public GameInstall(string gameRoot, string home = null)
        {
            Root = Path.GetFullPath(gameRoot);
            Home = home ?? DefaultHome;
            ExePath = GamePaths.FindExe(Root) ?? throw new FileNotFoundException("No u9.exe in " + Root);
            ExeId = FileId.Of(File.ReadAllBytes(ExePath));
            Recover();
        }

        // ---- state

        public sealed class State
        {
            public List<(string Id, string Version, string Name)> Active = new List<(string, string, string)>();
            /// <summary>Every file the manager wrote, with the hash it wrote.</summary>
            public Dictionary<string, string> Written = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool ExeCopy;
        }

        public State Read()
        {
            var s = new State();
            if (!File.Exists(Marker)) return s;
            var j = JsonNode.Parse(File.ReadAllText(Marker));
            foreach (var a in j["active"]?.AsArray() ?? new JsonArray()) s.Active.Add(((string)a["id"], (string)a["version"], (string)a["name"]));
            foreach (var kv in j["written"]?.AsObject() ?? new JsonObject()) s.Written[kv.Key] = (string)kv.Value;
            s.ExeCopy = (bool?)j["exe_copy"] ?? false;
            return s;
        }

        void WriteState(State s)
        {
            var j = new JsonObject
            {
                ["format"] = 1, ["exe"] = ExeId.Sha256,
                ["active"] = new JsonArray(s.Active.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["version"] = a.Version, ["name"] = a.Name }).ToArray()),
                ["written"] = new JsonObject(s.Written.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value))),
                ["exe_copy"] = s.ExeCopy,
            };
            if (s.Active.Count == 0 && s.Written.Count == 0 && !s.ExeCopy) { File.Delete(Marker); return; }
            SafeWrite(Marker, System.Text.Encoding.UTF8.GetBytes(j.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        }

        /// <summary>The original of a mod path: the vault's copy when the manager changed it, else the game's file (null if none).</summary>
        public byte[] ReadOriginal(string path)
        {
            string v = Path.Combine(Vault, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(v)) return File.ReadAllBytes(v);
            var f = GamePaths.Find(Root, path);
            return f != null ? File.ReadAllBytes(f) : null;
        }

        /// <summary>Files the manager wrote that have changed since (by the editor, by hand): they would be lost.</summary>
        public List<string> ChangedSince()
        {
            var s = Read();
            var list = new List<string>();
            foreach (var kv in s.Written)
            {
                var f = GamePaths.Find(Root, kv.Key);
                if (f == null || FileId.Hash(File.ReadAllBytes(f)) != kv.Value) list.Add(kv.Key);
            }
            return list;
        }

        // ---- applying

        /// <summary>What a set of mods would give here (nothing written).</summary>
        public StackResult Plan(IReadOnlyList<ModPackage> mods) => ModStack.Compose(mods, ReadOriginal, File.ReadAllBytes(ExePath));

        /// <summary>
        /// Makes the game folder what the mods (in order) give: every file a mod changes gets its new content, every file
        /// changed before and no longer gets its original back, and u9-mod.exe is written (or removed). All or nothing;
        /// refuses when the set clashes or a file the manager wrote was changed since (unless <paramref name="overwriteChanged"/>).
        /// </summary>
        public StackResult Apply(IReadOnlyList<ModPackage> mods, bool overwriteChanged = false, bool largeAddressAware = true)
        {
            var plan = Plan(mods);
            if (!plan.Ok) return plan;
            var drifted = ChangedSince();
            if (drifted.Count > 0 && !overwriteChanged)
            {
                plan.Clashes.Add("These files changed since the mod manager wrote them, and would be lost: " + string.Join(", ", drifted) +
                                 ". Make a mod of them first, or apply anyway to overwrite them.");
                return plan;
            }
            var state = Read();
            // what every touched file must become
            var want = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in state.Written.Keys.Union(plan.Files.Keys))
            {
                var target = GamePaths.Find(Root, path) ?? throw new FileNotFoundException($"The game has no {path}.");
                want[path] = plan.Files.TryGetValue(path, out var b) ? b : ReadOriginal(path);
            }
            // originals into the vault before anything changes
            foreach (var path in plan.Files.Keys)
            {
                string v = Path.Combine(Vault, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(v)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(v));
                File.Copy(GamePaths.Find(Root, path), v + ".part", true);
                File.Move(v + ".part", v, true);
            }
            // the new contents beside their targets, then the journal, then the renames
            var steps = new List<(string Temp, string Target)>();
            foreach (var kv in want)
            {
                string target = GamePaths.Find(Root, kv.Key);
                if (File.Exists(target) && new FileInfo(target).Length == kv.Value.Length && File.ReadAllBytes(target).AsSpan().SequenceEqual(kv.Value)) continue;
                string temp = target + NewSuffix;
                File.WriteAllBytes(temp, kv.Value);
                steps.Add((temp, target));
            }
            byte[] modExe = null;
            if (plan.ExePatches.Count > 0)
            {
                modExe = File.ReadAllBytes(ExePath);
                ExePatches.Apply(modExe, plan.ExePatches);
                if (largeAddressAware) ExePatches.SetLargeAddressAware(modExe);
                string exeTemp = Path.Combine(Root, GamePaths.ModdedExe + NewSuffix);
                File.WriteAllBytes(exeTemp, modExe);
                steps.Add((exeTemp, Path.Combine(Root, GamePaths.ModdedExe)));
            }
            Directory.CreateDirectory(Home);
            File.WriteAllText(Journal, new JsonObject
            {
                ["root"] = Root,
                ["steps"] = new JsonArray(steps.Select(s => (JsonNode)new JsonObject { ["temp"] = s.Temp, ["target"] = s.Target }).ToArray()),
            }.ToJsonString());
            foreach (var (temp, target) in steps) File.Move(temp, target, true);
            string modExePath = Path.Combine(Root, GamePaths.ModdedExe);
            if (modExe == null && state.ExeCopy && File.Exists(modExePath)) File.Delete(modExePath);
            var next = new State { ExeCopy = modExe != null };
            next.Active.AddRange(mods.Select(m => (m.Id, m.Version, m.Name)));
            foreach (var kv in plan.Files) next.Written[kv.Key] = FileId.Hash(kv.Value);
            WriteState(next);
            File.Delete(Journal);
            return plan;
        }

        /// <summary>Puts the original game back (applies no mods).</summary>
        public StackResult Original(bool overwriteChanged = false) => Apply(Array.Empty<ModPackage>(), overwriteChanged);

        /// <summary>Finishes a change cut off half way: the renames it had begun are completed (the new files were all written before any).</summary>
        public void Recover()
        {
            if (!File.Exists(Journal)) return;
            var j = JsonNode.Parse(File.ReadAllText(Journal));
            foreach (var s in j["steps"]?.AsArray() ?? new JsonArray())
            {
                string temp = (string)s["temp"], target = (string)s["target"];
                if (File.Exists(temp)) File.Move(temp, target, true);
            }
            File.Delete(Journal);
        }

        /// <summary>Starts the game: u9-mod.exe when the active mods patch the executable, else u9.exe.</summary>
        public Process Launch()
        {
            string modExe = Path.Combine(Root, GamePaths.ModdedExe);
            string exe = Read().ExeCopy && File.Exists(modExe) ? modExe : ExePath;
            return Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = Root, UseShellExecute = false });
        }

        static void SafeWrite(string path, byte[] data)
        {
            File.WriteAllBytes(path + ".part", data);
            File.Move(path + ".part", path, true);
        }
    }
}
