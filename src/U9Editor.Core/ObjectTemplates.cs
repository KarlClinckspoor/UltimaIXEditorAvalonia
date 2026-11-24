using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>
    /// Objects of every type as the game's maps hold them, for new objects of a type the open map has none of: the
    /// record of the first one found in any fixed.N or nonfixed.N (flags, model and such as the game set them), and
    /// how many each kind of file holds. Read once, the first time it is asked.
    /// </summary>
    public sealed class ObjectTemplates
    {
        readonly string root;
        Dictionary<(ObjectFileKind, int), byte[]> first;
        Dictionary<int, (int Fixed, int Nonfixed)> counts;

        public ObjectTemplates(string gameRoot) { root = gameRoot; }

        void Load()
        {
            if (first != null) return;
            first = new Dictionary<(ObjectFileKind, int), byte[]>();
            counts = new Dictionary<int, (int, int)>();
            foreach (var (folder, name, kind) in new[] { ("static", "fixed", ObjectFileKind.Fixed), ("runtime", "nonfixed", ObjectFileKind.Nonfixed) })
            {
                string dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                // by map number, so the record taken is the same every time
                var files = Directory.EnumerateFiles(dir, name + ".*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                    .Select(f => (File: f, N: int.TryParse(Path.GetExtension(f).TrimStart('.'), out int n) ? n : -1))
                    .Where(f => f.N >= 0).OrderBy(f => f.N);
                foreach (var (f, _) in files)
                {
                    ObjectMap m;
                    try { m = ObjectMap.Load(f, kind); }
                    catch (Exception e) when (e is InvalidDataException || e is IOException || e is ArgumentException) { continue; }
                    foreach (var o in m.Objects)
                    {
                        first.TryAdd((kind, o.Type), o.Raw);
                        var c = counts.GetValueOrDefault(o.Type);
                        counts[o.Type] = kind == ObjectFileKind.Fixed ? (c.Fixed + 1, c.Nonfixed) : (c.Fixed, c.Nonfixed + 1);
                    }
                }
            }
        }

        /// <summary>A record of an object of the type in a file of that kind on any map, or null.</summary>
        public byte[] Find(ObjectFileKind kind, int type)
        {
            Load();
            return first.TryGetValue((kind, type), out var r) ? (byte[])r.Clone() : null;
        }

        /// <summary>How many objects of the type the game's fixed and nonfixed files hold, all maps together.</summary>
        public (int Fixed, int Nonfixed) Count(int type)
        {
            Load();
            return counts.GetValueOrDefault(type);
        }
    }
}
