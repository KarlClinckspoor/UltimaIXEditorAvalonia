using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Terrain;

namespace U9Editor.Core
{
    /// <summary>One map of the game: its number and the name from its terrain header.</summary>
    public sealed class MapEntry
    {
        public int Number { get; init; }
        public string Name { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string TerrainPath { get; init; }
        public override string ToString() => $"{Number,3}  {Name}  ({Width}x{Height})";
    }

    /// <summary>
    /// An Ultima IX install (the folder holding static/ and runtime/). Finds the maps by their terrain files.
    /// </summary>
    public sealed class GameFolder
    {
        public string Root { get; }
        public string StaticFolder => Path.Combine(Root, "static");

        GameFolder(string root) { Root = root; }

        /// <summary>The install at <paramref name="path"/>, or null when it has no static/terrain.* files.</summary>
        public static GameFolder TryOpen(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string s = Path.Combine(path, "static");
            if (!Directory.Exists(s)) return null;
            if (!Directory.EnumerateFiles(s, "terrain.*").Any()) return null;
            return new GameFolder(Path.GetFullPath(path));
        }

        /// <summary>Looks for a gamedata folder next to the program or in any folder above it.</summary>
        public static GameFolder FindNear(string start)
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var g = TryOpen(Path.Combine(d.FullName, "gamedata"));
                if (g != null) return g;
            }
            return null;
        }

        /// <summary>All maps with terrain, by number. Maps without terrain (width 0) are left out.</summary>
        public List<MapEntry> Maps()
        {
            var list = new List<MapEntry>();
            foreach (string f in Directory.EnumerateFiles(StaticFolder, "terrain.*"))
            {
                if (!int.TryParse(Path.GetExtension(f).TrimStart('.'), out int n)) continue;
                TerrainMap t;
                try { t = TerrainMap.Load(f); }
                catch (InvalidDataException) { continue; }
                if (t.Width == 0) continue;
                list.Add(new MapEntry { Number = n, Name = t.Name, Width = t.Width, Height = t.Height, TerrainPath = f });
            }
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            return list;
        }
    }
}
