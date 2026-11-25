using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace U9.Mods
{
    /// <summary>The mod manager's settings (Home\settings.json): game folders, the catalog URL, and per game the mods chosen in order.</summary>
    public sealed class ManagerSettings
    {
        public List<string> Games = new List<string>();
        public string CatalogUrl = "";
        /// <summary>Per game folder, the mod ids ticked, in the order they apply.</summary>
        public Dictionary<string, List<string>> Chosen = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public bool LargeAddressAware = true;

        public static string PathIn(string home) => Path.Combine(home, "settings.json");

        public static ManagerSettings Load(string home)
        {
            var s = new ManagerSettings();
            string p = PathIn(home);
            if (!File.Exists(p)) return s;
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(p));
                s.Games = j["games"]?.AsArray().Select(g => (string)g).Where(g => g != null).ToList() ?? s.Games;
                s.CatalogUrl = (string)j["catalog"] ?? "";
                s.LargeAddressAware = (bool?)j["large_address_aware"] ?? true;
                foreach (var kv in j["chosen"]?.AsObject() ?? new JsonObject())
                    s.Chosen[kv.Key] = kv.Value.AsArray().Select(x => (string)x).Where(x => x != null).ToList();
            }
            catch (JsonException) { }
            return s;
        }

        public void Save(string home)
        {
            Directory.CreateDirectory(home);
            var j = new JsonObject
            {
                ["games"] = new JsonArray(Games.Select(g => (JsonNode)g).ToArray()),
                ["catalog"] = CatalogUrl,
                ["large_address_aware"] = LargeAddressAware,
                ["chosen"] = new JsonObject(Chosen.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)new JsonArray(kv.Value.Select(v => (JsonNode)v).ToArray())))),
            };
            string p = PathIn(home);
            File.WriteAllText(p + ".part", j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(p + ".part", p, true);
        }
    }
}
