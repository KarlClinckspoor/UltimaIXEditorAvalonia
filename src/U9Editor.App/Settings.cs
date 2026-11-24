using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace U9Editor.App
{
    /// <summary>What the editor remembers between runs, in %AppData%/U9Editor/settings.json.</summary>
    public sealed class Settings
    {
        public string GameFolder { get; set; }
        public int LastMap { get; set; } = 9;
        public bool NorthIsRowZero { get; set; }
        /// <summary>View > Theme: "Light" (the default) or "Dark".</summary>
        public string Theme { get; set; } = "Light";
        /// <summary>Named places; null until first used, then filled with a few starting ones.</summary>
        public List<Bookmark> Bookmarks { get; set; }

        public static List<Bookmark> StartingBookmarks() => new List<Bookmark>
        {
            new Bookmark { Name = "Castle British", Map = 9, X = 502, Row = 554 },
            new Bookmark { Name = "Moonglow", Map = 9, X = 715, Row = 420 },
            new Bookmark { Name = "Bridge west of Britain (test spot)", Map = 9, X = 467, Row = 505 },
            new Bookmark { Name = "Cave to New Magincia", Map = 9, X = 573, Row = 262 },
        };

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "U9Editor");
        static string FilePath => Path.Combine(Folder, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException) { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }

    /// <summary>A named place: map, point looked at, and how the camera looked.</summary>
    public sealed class Bookmark
    {
        public string Name { get; set; }
        public int Map { get; set; }
        public float X { get; set; }
        public float Row { get; set; }
        public float Distance { get; set; } = 40;
        public float Pitch { get; set; } = 50;
        public float Yaw { get; set; }
        public string MapName { get; set; }
        public override string ToString() => $"{Name}   (map {Map}{(string.IsNullOrEmpty(MapName) ? "" : " " + MapName)}, {X:0}, {Row:0})";
    }
}
