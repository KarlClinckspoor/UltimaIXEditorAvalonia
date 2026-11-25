using System;
using System.IO;
using System.Linq;

namespace U9.Mods
{
    /// <summary>
    /// Which files a mod may change (docs/mod-format.md, rule 1) and how its paths are written: lower case,
    /// '/'-separated, relative to the game folder, under static/ or runtime/. On disk a path is found whatever its case.
    /// </summary>
    public static class GamePaths
    {
        public const string Exe = "u9.exe", ModdedExe = "u9-mod.exe";
        static readonly string[] Folders = { "static/", "runtime/" };

        /// <summary>A path as a mod writes it, or null when it is not one a mod may change.</summary>
        public static string Normalise(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Replace('\\', '/').Trim().ToLowerInvariant();
            if (p.StartsWith("/") || p.Contains(':')) return null;
            var parts = p.Split('/');
            if (parts.Any(s => s.Length == 0 || s == "." || s == "..")) return null;
            if (!Folders.Any(f => p.StartsWith(f)) || parts.Length < 2) return null;
            if (p.EndsWith(".orig") || p.EndsWith(".u9mm-new") || p.EndsWith(".new")) return null;
            return p;
        }

        public static bool IsAllowed(string path) => Normalise(path) == path;

        /// <summary>The file on disk for a mod path (any case), or null when the game has no such file.</summary>
        public static string Find(string gameRoot, string modPath)
        {
            string at = gameRoot;
            foreach (var part in modPath.Split('/'))
            {
                if (!Directory.Exists(at)) return null;
                var hit = Directory.EnumerateFileSystemEntries(at).FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
                if (hit == null) return null;
                at = hit;
            }
            return File.Exists(at) ? at : null;
        }

        /// <summary>The executable on disk (any case), or null.</summary>
        public static string FindExe(string gameRoot) =>
            Directory.Exists(gameRoot) ? Directory.EnumerateFiles(gameRoot).FirstOrDefault(f => string.Equals(Path.GetFileName(f), Exe, StringComparison.OrdinalIgnoreCase)) : null;

        /// <summary>The mod path of a file inside the game folder, or null when a mod may not change it.</summary>
        public static string Relative(string gameRoot, string file)
        {
            var rel = Path.GetRelativePath(gameRoot, file);
            return Normalise(rel);
        }
    }
}
