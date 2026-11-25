using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace U9.Mods
{
    /// <summary>
    /// Makes a mod from a changed game folder: each changed file against its original, which is the editor's
    /// "name.orig" copy beside it (the first version it overwrote) or a file in a folder of originals.
    /// </summary>
    public static class ModMaker
    {
        /// <summary>The changed files of a folder the editor saved into: (mod path, original, edited) for every file with a .orig copy.</summary>
        public static List<(string Path, string Original, string Edited)> ChangedByEditor(string gameRoot)
        {
            var list = new List<(string, string, string)>();
            foreach (var folder in new[] { "static", "runtime" })
            {
                var dir = Directory.EnumerateDirectories(gameRoot).FirstOrDefault(d => string.Equals(Path.GetFileName(d), folder, StringComparison.OrdinalIgnoreCase));
                if (dir == null) continue;
                foreach (var orig in Directory.EnumerateFiles(dir, "*.orig", SearchOption.AllDirectories))
                {
                    string edited = orig[..^5];
                    string rel = GamePaths.Relative(gameRoot, edited);
                    if (rel == null || !File.Exists(edited)) continue;
                    list.Add((rel, orig, edited));
                }
            }
            return list.OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// A mod from the folder's changes against its .orig copies (and u9.exe's patches: the editor's u9.exe.patches.json, or u9.exe against u9.exe.orig). Files
        /// that came back to their original are left out. Every delta is checked to rebuild its file exactly.
        /// </summary>
        public static ModPackage FromEditorCopies(string gameRoot, ModPackage meta)
        {
            var files = ChangedByEditor(gameRoot).Select(t => (t.Path, (Func<byte[]>)(() => File.ReadAllBytes(t.Original)), (Func<byte[]>)(() => File.ReadAllBytes(t.Edited))));
            string exe = GamePaths.FindExe(gameRoot);
            (byte[], byte[])? exePair = exe != null && File.Exists(exe + ".orig") ? (File.ReadAllBytes(exe + ".orig"), File.ReadAllBytes(exe)) : null;
            List<ExePatch> pending = null;
            if (exe != null && File.Exists(ExePatchFile.PathIn(gameRoot)))
            {
                var original = exePair?.Item1 ?? File.ReadAllBytes(exe);
                pending = ExePatchFile.Load(ExePatchFile.PathIn(gameRoot), original);
                if (exePair == null) exePair = (original, original);
            }
            return Make(meta, files, exePair, pending);
        }

        /// <summary>A mod from (path, original, edited) triples and an optional (original, edited) executable.</summary>
        public static ModPackage Make(ModPackage meta, IEnumerable<(string Path, Func<byte[]> Original, Func<byte[]> Edited)> files, (byte[] Original, byte[] Edited)? exe,
            IReadOnlyList<ExePatch> exePatches = null)
        {
            var m = new ModPackage
            {
                Id = meta.Id, Name = meta.Name, Version = meta.Version, Description = meta.Description,
                Authors = meta.Authors.ToList(), MadeWith = meta.MadeWith,
            };
            foreach (var (path, readOriginal, readEdited) in files)
            {
                string rel = GamePaths.Normalise(path) ?? throw new InvalidOperationException($"A mod may not change {path}.");
                var original = readOriginal();
                var edited = readEdited();
                if (original.AsSpan().SequenceEqual(edited)) continue;
                m.Files.Add(new ModFile
                {
                    Path = rel, Base = FileId.Of(original), Target = FileId.Of(edited),
                    DeltaPath = "deltas/" + rel + ".u9d", Delta = Delta.Make(original, edited),
                });
            }
            if (exe is { } e && (!e.Original.AsSpan().SequenceEqual(e.Edited) || exePatches?.Count > 0))
            {
                m.ExeBase = FileId.Of(e.Original);
                if (exePatches?.Count > 0)
                {
                    // The editor's notes are kept; an edited u9.exe as well is folded in first.
                    var diff = ExePatches.Diff(e.Original, e.Edited);
                    foreach (var p in exePatches)
                    {
                        if (diff.Any(d => d.Offset < p.End && p.Offset < d.End)) throw new InvalidOperationException($"u9.exe and {ExePatchFile.Name} both change 0x{p.Offset:X}.");
                    }
                    m.ExePatches = diff.Concat(exePatches.Select(p => new ExePatch { Offset = p.Offset, Old = p.Old, New = p.New, Note = p.Note })).OrderBy(p => p.Offset).ToList();
                }
                else m.ExePatches = ExePatches.Diff(e.Original, e.Edited);
                var clashes = ExePatches.U9glClashes(e.Original, m.ExePatches);
                if (clashes.Count > 0) throw new InvalidOperationException("The executable changes touch the code U9GL checks: " + string.Join("; ", clashes) + ".");
            }
            m.Validate();
            return m;
        }
    }
}
