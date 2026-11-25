using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace U9.Mods
{
    /// <summary>
    /// The editor's pending changes to u9.exe, kept beside it as u9.exe.patches.json instead of in u9.exe (which U9GL
    /// identifies by its hash): the executable they were made for and the patches with notes. "Export changes as mod"
    /// puts them into the mod; the mod manager writes them into u9-mod.exe.
    /// </summary>
    public static class ExePatchFile
    {
        public const string Name = "u9.exe.patches.json";

        public static string PathIn(string gameRoot) => Path.Combine(gameRoot, Name);

        public static void Save(string path, FileId exe, IReadOnlyList<ExePatch> patches)
        {
            if (patches.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            var o = new JsonObject
            {
                ["format"] = 1,
                ["exe"] = new JsonObject { ["sha256"] = exe.Sha256, ["size"] = exe.Size },
                ["patches"] = new JsonArray(patches.OrderBy(p => p.Offset).Select(p => (JsonNode)new JsonObject
                {
                    ["offset"] = p.Offset, ["old"] = Convert.ToHexStringLower(p.Old), ["new"] = Convert.ToHexStringLower(p.New), ["note"] = p.Note ?? "",
                }).ToArray()),
            };
            string temp = path + ".new";
            File.WriteAllText(temp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }

        /// <summary>The patches in a file, checked against the executable they are for. Null when there is no file.</summary>
        public static List<ExePatch> Load(string path, byte[] exe)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(path));
                var id = new FileId((string)j["exe"]["sha256"], (long)j["exe"]["size"]);
                if (!id.Matches(exe)) throw new InvalidDataException($"{Name} was made for another u9.exe.");
                var list = (j["patches"]?.AsArray() ?? new JsonArray()).Select(p => new ExePatch
                {
                    Offset = (int)p["offset"], Old = Convert.FromHexString((string)p["old"]), New = Convert.FromHexString((string)p["new"]),
                    Note = (string)p["note"] ?? "",
                }).ToList();
                foreach (var p in list)
                    if (p.Old.Length != p.New.Length || p.Offset < 0 || p.End > exe.Length || !exe.AsSpan(p.Offset, p.Old.Length).SequenceEqual(p.Old))
                        throw new InvalidDataException($"{Name}: the patch at 0x{p.Offset:X} does not fit u9.exe.");
                return list;
            }
            catch (Exception ex) when (ex is JsonException || ex is NullReferenceException || ex is FormatException || ex is InvalidOperationException)
            {
                throw new InvalidDataException($"{Name} is damaged: {ex.Message}");
            }
        }
    }
}
