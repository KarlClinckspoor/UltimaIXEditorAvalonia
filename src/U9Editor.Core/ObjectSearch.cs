using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>
    /// Finds objects by name or number, on the open map or on every map of the game.
    /// <para>A query is words, all of which must match: plain words are looked for in the type's name (any case), a
    /// whole number matches the type number or a word of the name, <c>#n</c> an object's id on the open map,
    /// <c>link:n</c> a nonfixed object's link, <c>fixed</c> / <c>nonfixed</c> the file.</para>
    /// </summary>
    public sealed class ObjectQuery
    {
        readonly List<string> words = new List<string>();
        readonly List<int> numbers = new List<int>();
        int? id, link;
        ObjectFileKind? kind;

        public bool IsEmpty => words.Count == 0 && numbers.Count == 0 && id == null && link == null && kind == null;

        public static ObjectQuery Parse(string text)
        {
            var q = new ObjectQuery();
            foreach (var raw in (text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = raw.ToLowerInvariant();
                if (w.Length > 1 && w[0] == '#' && int.TryParse(w.AsSpan(1), out int i)) q.id = i;
                else if (w.StartsWith("link:") && int.TryParse(w.AsSpan(5), out int l)) q.link = l;
                else if (w == "fixed") q.kind = ObjectFileKind.Fixed;
                else if (w == "nonfixed") q.kind = ObjectFileKind.Nonfixed;
                else if (int.TryParse(w, out int n)) q.numbers.Add(n);
                else q.words.Add(w);
            }
            return q;
        }

        public bool Matches(int objectId, ObjectFileKind objectKind, int type, int objectLink, string name)
        {
            if (IsEmpty) return false;
            if (id is int i && objectId != i) return false;
            if (kind is ObjectFileKind k && objectKind != k) return false;
            if (link is int l && (objectKind != ObjectFileKind.Nonfixed || objectLink != l)) return false;
            string n = (name ?? "").ToLowerInvariant();
            foreach (int num in numbers)
                if (type != num && !n.Split(' ', '-', '(', ')').Contains(num.ToString())) return false;
            foreach (var w in words) if (!n.Contains(w)) return false;
            return true;
        }

        public bool Matches(SceneObject o) =>
            !o.Source.Removed && Matches(o.Id, o.Kind, o.Source.Type, o.Kind == ObjectFileKind.Nonfixed ? o.Source.Link : -1, o.Name);
    }

    /// <summary>An object found on some map.</summary>
    public sealed class FoundObject
    {
        public int Map { get; init; }
        public string MapName { get; init; }
        public ObjectFileKind Kind { get; init; }
        public int Type { get; init; }
        public string Name { get; init; }
        /// <summary>Position in points (x east, row north) and height in object units.</summary>
        public float X { get; init; }
        public float Row { get; init; }
        public int Z { get; init; }
        /// <summary>The scene object when it is on the open map, else null.</summary>
        public SceneObject Object { get; init; }

        public override string ToString() =>
            $"{(Object == null ? $"map {Map} " : "")}{Name ?? "type " + Type} ({Type}{(Kind == ObjectFileKind.Nonfixed ? ", nonfixed" : "")}) at {X:0.#}, {Row:0.#}";
    }

    /// <summary>Searches; the other maps' object files are read once, the first time every map is searched.</summary>
    public sealed class ObjectSearch
    {
        readonly string root;
        readonly TypeNames names;
        readonly Dictionary<(int Map, ObjectFileKind Kind), ObjectMap> files = new Dictionary<(int, ObjectFileKind), ObjectMap>();
        bool read;

        public ObjectSearch(string gameRoot, TypeNames names) { root = gameRoot; this.names = names; }

        /// <summary>The objects of the open map that match, in id order.</summary>
        public static List<SceneObject> InMap(SceneObjects objects, ObjectQuery q) =>
            objects == null || q.IsEmpty ? new List<SceneObject>() : objects.All.Where(q.Matches).ToList();

        /// <summary>
        /// Matches on every map, at most <paramref name="limit"/>. <paramref name="openFiles"/> gives the files of maps
        /// open in the editor (with their unsaved edits), or nulls to read them from the game folder.
        /// </summary>
        public List<FoundObject> AllMaps(ObjectQuery q, IReadOnlyList<MapEntry> maps, Func<int, (ObjectMap Fixed, ObjectMap Nonfixed)?> openFiles, int limit = 2000)
        {
            var found = new List<FoundObject>();
            if (q.IsEmpty) return found;
            ReadAll();
            var mapNames = maps?.ToDictionary(m => m.Number, m => m.Name) ?? new Dictionary<int, string>();
            var numbers = files.Keys.Select(k => k.Map).Concat(mapNames.Keys).Distinct().OrderBy(n => n);
            foreach (int map in numbers)
            {
                var open = openFiles?.Invoke(map);
                foreach (var kind in new[] { ObjectFileKind.Fixed, ObjectFileKind.Nonfixed })
                {
                    var m = open is { } o ? (kind == ObjectFileKind.Fixed ? o.Fixed : o.Nonfixed) : files.GetValueOrDefault((map, kind));
                    if (m == null) continue;
                    foreach (var ob in m.Objects)
                    {
                        if (ob.Removed || !q.Matches(-1, kind, ob.Type, kind == ObjectFileKind.Nonfixed ? ob.Link : -1, names?[ob.Type])) continue;
                        found.Add(new FoundObject
                        {
                            Map = map, MapName = mapNames.GetValueOrDefault(map), Kind = kind, Type = ob.Type, Name = names?[ob.Type],
                            X = ob.MapX / MapSpace.ObjectUnitsPerPoint, Row = ob.MapY / MapSpace.ObjectUnitsPerPoint, Z = ob.Z,
                        });
                        if (found.Count >= limit) return found;
                    }
                }
            }
            return found;
        }

        void ReadAll()
        {
            if (read) return;
            read = true;
            foreach (var (folder, name, kind) in new[] { ("static", "fixed", ObjectFileKind.Fixed), ("runtime", "nonfixed", ObjectFileKind.Nonfixed) })
            {
                string dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, name + ".*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
                {
                    if (!int.TryParse(Path.GetExtension(f).TrimStart('.'), out int n)) continue;
                    try { files[(n, kind)] = ObjectMap.Load(f, kind); }
                    catch (Exception e) when (e is InvalidDataException || e is IOException || e is ArgumentException) { }
                }
            }
        }
    }
}
