using System;
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
}
