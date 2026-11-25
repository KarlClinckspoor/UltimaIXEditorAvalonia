using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Flx;

namespace U9.Mods
{
    /// <summary>What a set of mods gives: each changed file's new content, the executable patches, and any clashes.</summary>
    public sealed class StackResult
    {
        public Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public List<ExePatch> ExePatches = new List<ExePatch>();
        /// <summary>Why the set cannot be applied (empty when it can).</summary>
        public List<string> Clashes = new List<string>();
        /// <summary>How each file came about, for showing: "mod", "merged bytes", "merged records".</summary>
        public Dictionary<string, string> How = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool Ok => Clashes.Count == 0;
    }

    /// <summary>
    /// Several mods over one game (docs/mod-format.md, rule 6): each mod's delta is applied to the original, and where two
    /// change the same file their results are merged byte by byte (same length) or record by record (FLX archives).
    /// A clash anywhere fails the whole set.
    /// </summary>
    public static class ModStack
    {
        /// <param name="readOriginal">The original of a mod path (null when the game has no such file).</param>
        /// <param name="originalExe">The original u9.exe (null when the game has none).</param>
        public static StackResult Compose(IReadOnlyList<ModPackage> mods, Func<string, byte[]> readOriginal, byte[] originalExe)
        {
            var r = new StackResult();
            var ids = mods.GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var id in ids) r.Clashes.Add($"{id} is in the list twice.");
            foreach (var path in mods.SelectMany(m => m.Files).Select(f => f.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            {
                var original = readOriginal(path);
                if (original == null) { r.Clashes.Add($"The game has no {path}, which {string.Join(", ", mods.Where(m => m.Files.Any(f => f.Path == path)).Select(m => m.Name))} change."); continue; }
                var results = new List<(ModPackage Mod, byte[] Bytes)>();
                foreach (var m in mods)
                {
                    var f = m.Files.FirstOrDefault(x => x.Path == path);
                    if (f == null) continue;
                    try { results.Add((m, f.ApplyTo(original))); }
                    catch (InvalidDataException ex) { r.Clashes.Add($"{m.Name}: {ex.Message}"); }
                }
                if (results.Count == 0) continue;
                if (results.Count == 1) { r.Files[path] = results[0].Bytes; r.How[path] = results[0].Mod.Name; continue; }
                var merged = results.All(x => x.Bytes.Length == original.Length) ? MergeBytes(path, original, results, r)
                           : path.EndsWith(".flx", StringComparison.Ordinal) ? MergeRecords(path, original, results, r)
                           : Clash(path, results, r);
                if (merged != null) r.Files[path] = merged;
            }
            ComposeExe(mods, originalExe, r);
            return r;
        }

        static byte[] Clash(string path, List<(ModPackage Mod, byte[] Bytes)> results, StackResult r)
        {
            r.Clashes.Add($"{string.Join(" and ", results.Select(x => x.Mod.Name))} all change {path} in ways that cannot be combined (its length changes).");
            return null;
        }

        static byte[] MergeBytes(string path, byte[] original, List<(ModPackage Mod, byte[] Bytes)> results, StackResult r)
        {
            var output = (byte[])original.Clone();
            var owner = new Dictionary<int, int>();          // byte offset -> which result set it
            for (int k = 0; k < results.Count; k++)
            {
                var b = results[k].Bytes;
                int i = 0;
                while (i < b.Length)
                {
                    i += original.AsSpan(i).CommonPrefixLength(b.AsSpan(i));
                    if (i >= b.Length) break;
                    if (owner.TryGetValue(i, out int other) && output[i] != b[i])
                    {
                        r.Clashes.Add($"{results[other].Mod.Name} and {results[k].Mod.Name} change the same bytes of {path} (at 0x{i:X}).");
                        return null;
                    }
                    owner[i] = k;
                    output[i] = b[i];
                    i++;
                }
            }
            r.How[path] = "merged bytes of " + string.Join(", ", results.Select(x => x.Mod.Name));
            return output;
        }

        static byte[] MergeRecords(string path, byte[] original, List<(ModPackage Mod, byte[] Bytes)> results, StackResult r)
        {
            FlxArchive baseArchive;
            var archives = new List<FlxArchive>();
            try
            {
                baseArchive = FlxArchive.Open(new MemoryStream(original, false));
                foreach (var x in results) archives.Add(FlxArchive.Open(new MemoryStream(x.Bytes, false)));
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is ArgumentException)
            {
                return Clash(path, results, r);
            }
            using (baseArchive)
            {
                var changes = new Dictionary<int, (int Who, byte[] Bytes)>();
                int count = baseArchive.Count;
                for (int k = 0; k < archives.Count; k++)
                {
                    var a = archives[k];
                    count = Math.Max(count, a.Count);
                    for (int i = 0; i < Math.Max(a.Count, baseArchive.Count); i++)
                    {
                        var mine = i < a.Count ? a.Read(i) : null;
                        var was = i < baseArchive.Count ? baseArchive.Read(i) : null;
                        if (Same(mine, was)) continue;
                        if (changes.TryGetValue(i, out var other) && !Same(other.Bytes, mine))
                        {
                            r.Clashes.Add($"{results[other.Who].Mod.Name} and {results[k].Mod.Name} change the same record ({i}) of {path}.");
                            foreach (var x in archives) x.Dispose();
                            return null;
                        }
                        changes[i] = (k, mine);
                    }
                }
                foreach (var x in archives) x.Dispose();
                if (count > baseArchive.Count) baseArchive.Resize(count);
                foreach (var kv in changes) baseArchive.Write(kv.Key, kv.Value.Bytes);
                using var ms = new MemoryStream();
                baseArchive.Save(ms, FlxLayout.Preserve);
                r.How[path] = $"merged records of {string.Join(", ", results.Select(x => x.Mod.Name))} ({changes.Count} records)";
                return ms.ToArray();
            }
        }

        static bool Same(byte[] a, byte[] b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

        static void ComposeExe(IReadOnlyList<ModPackage> mods, byte[] originalExe, StackResult r)
        {
            var withPatches = mods.Where(m => m.ChangesExe).ToList();
            if (withPatches.Count == 0) return;
            if (originalExe == null) { r.Clashes.Add("The game has no u9.exe to patch."); return; }
            var exeId = FileId.Of(originalExe);
            var all = new List<(ModPackage Mod, ExePatch Patch)>();
            foreach (var m in withPatches)
            {
                if (m.ExeBase != exeId) { r.Clashes.Add($"{m.Name} patches another u9.exe (made for {m.ExeBase.Value.Size} bytes, SHA-256 {m.ExeBase.Value.Sha256[..12]}...)."); continue; }
                foreach (var p in m.ExePatches)
                {
                    if (p.End > originalExe.Length || !originalExe.AsSpan(p.Offset, p.Old.Length).SequenceEqual(p.Old))
                        r.Clashes.Add($"{m.Name}: u9.exe does not have the bytes its patch at 0x{p.Offset:X} expects.");
                    else all.Add((m, p));
                }
                foreach (var c in ExePatches.U9glClashes(originalExe, m.ExePatches)) r.Clashes.Add($"{m.Name}: {c}.");
            }
            all.Sort((a, b) => a.Patch.Offset.CompareTo(b.Patch.Offset));
            for (int k = 0; k < all.Count; k++)
            {
                var (m, p) = all[k];
                var prev = r.ExePatches.LastOrDefault();
                if (prev != null && prev.End > p.Offset)
                {
                    if (prev.Offset == p.Offset && prev.New.AsSpan().SequenceEqual(p.New)) continue;      // the same patch twice
                    var owner = all.Take(k).Last(x => x.Patch == prev).Mod;
                    r.Clashes.Add($"{owner.Name} and {m.Name} patch the same bytes of u9.exe (at 0x{p.Offset:X}).");
                    continue;
                }
                r.ExePatches.Add(p);
            }
        }
    }
}
