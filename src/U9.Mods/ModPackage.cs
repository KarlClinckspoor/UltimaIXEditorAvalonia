using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace U9.Mods
{
    /// <summary>A file's identity: SHA-256 (lower-case hex) and size.</summary>
    public readonly record struct FileId(string Sha256, long Size)
    {
        public static FileId Of(byte[] data) => new FileId(Hash(data), data.Length);
        public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        public bool Matches(byte[] data) => data.Length == Size && Hash(data) == Sha256;
    }

    /// <summary>One changed file of a mod: what it was made from, what it gives, and the delta between.</summary>
    public sealed class ModFile
    {
        public string Path;
        public FileId Base, Target;
        public string DeltaPath;
        public byte[] Delta;

        /// <summary>The changed file from its original, every hash checked.</summary>
        public byte[] ApplyTo(byte[] original)
        {
            if (!Base.Matches(original)) throw new InvalidDataException($"{Path} is not the file the mod was made from (another version of the game, or already changed).");
            var result = Mods.Delta.Apply(original, Delta);
            if (!Target.Matches(result)) throw new InvalidDataException($"{Path}: the mod's delta does not give the file it promises.");
            return result;
        }
    }

    /// <summary>
    /// A mod package (.u9mod; docs/mod-format.md): a zip with mod.json and a delta per changed file, plus patches to the
    /// executable. Packages are untrusted: reading one checks every rule and hash, and refuses it on any fault.
    /// </summary>
    public sealed class ModPackage
    {
        public const int Format = 1;
        public const string Extension = ".u9mod";
        const long MaxEntry = 512L << 20, MaxTotal = 1L << 30;
        static readonly Regex IdRule = new Regex("^[a-z0-9._-]{3,64}$");

        public string Id = "", Name = "", Version = "1.0.0", Description = "", MadeWith = "";
        public List<string> Authors = new List<string>();
        public List<ModFile> Files = new List<ModFile>();
        /// <summary>The executable the patches were made against (null: no patches).</summary>
        public FileId? ExeBase;
        public List<ExePatch> ExePatches = new List<ExePatch>();

        public bool ChangesExe => ExePatches.Count > 0;
        public override string ToString() => $"{Name} {Version} ({Id})";

        public static bool IsValidId(string id) => id != null && IdRule.IsMatch(id);

        /// <summary>What the mod changes, in a line.</summary>
        public string Summary()
        {
            var parts = new List<string>();
            if (Files.Count > 0) parts.Add(Files.Count == 1 ? $"1 file ({Files[0].Path})" : $"{Files.Count} files");
            if (ChangesExe) parts.Add($"{ExePatches.Count} executable patch{(ExePatches.Count == 1 ? "" : "es")}");
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }

        // ---- writing

        public void Write(string path)
        {
            Validate();
            string temp = path + ".part";
            using (var fs = File.Create(temp))
                Write(fs);
            File.Move(temp, path, true);
        }

        public void Write(Stream output)
        {
            Validate();
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var json = Manifest().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            using (var w = new StreamWriter(zip.CreateEntry("mod.json").Open())) w.Write(json);
            foreach (var f in Files)
                using (var s = zip.CreateEntry(f.DeltaPath, CompressionLevel.Optimal).Open()) s.Write(f.Delta);
        }

        JsonObject Manifest()
        {
            var o = new JsonObject
            {
                ["format"] = Format, ["game"] = "u9", ["id"] = Id, ["name"] = Name, ["version"] = Version,
                ["authors"] = new JsonArray(Authors.Select(a => (JsonNode)a).ToArray()),
                ["description"] = Description, ["made_with"] = MadeWith,
                ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject
                {
                    ["path"] = f.Path, ["base"] = IdJson(f.Base), ["target"] = IdJson(f.Target),
                    ["delta"] = new JsonObject { ["path"] = f.DeltaPath, ["sha256"] = FileId.Hash(f.Delta) },
                }).ToArray()),
            };
            if (ChangesExe)
                o["exe"] = new JsonObject
                {
                    ["base"] = IdJson(ExeBase.Value),
                    ["patches"] = new JsonArray(ExePatches.Select(p => (JsonNode)new JsonObject
                    {
                        ["offset"] = p.Offset, ["old"] = Convert.ToHexStringLower(p.Old), ["new"] = Convert.ToHexStringLower(p.New), ["note"] = p.Note ?? "",
                    }).ToArray()),
                };
            return o;
        }

        static JsonObject IdJson(FileId f) => new JsonObject { ["sha256"] = f.Sha256, ["size"] = f.Size };

        /// <summary>Throws when the package breaks a rule (ids, paths, patches).</summary>
        public void Validate()
        {
            if (!IsValidId(Id)) throw new InvalidDataException($"\"{Id}\" is not a mod id: 3 to 64 lower-case letters, digits, '.', '_' or '-'.");
            if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("The mod has no name.");
            var seen = new HashSet<string>();
            foreach (var f in Files)
            {
                if (!GamePaths.IsAllowed(f.Path)) throw new InvalidDataException($"A mod may not change \"{f.Path}\" (only files under static/ and runtime/, and u9.exe by patches).");
                if (!seen.Add(f.Path)) throw new InvalidDataException($"{f.Path} is in the mod twice.");
                if (f.DeltaPath != "deltas/" + f.Path + ".u9d") throw new InvalidDataException($"{f.Path}'s delta is not where it belongs.");
                if (f.Delta == null) throw new InvalidDataException($"{f.Path} has no delta.");
            }
            if (ChangesExe && ExeBase == null) throw new InvalidDataException("The executable patches do not say which u9.exe they were made for.");
            var sorted = ExePatches.OrderBy(p => p.Offset).ToList();
            for (int k = 0; k < sorted.Count; k++)
            {
                var p = sorted[k];
                if (p.Old == null || p.New == null || p.Old.Length != p.New.Length || p.New.Length == 0 || p.New.Length > 4096 || p.Offset < 0)
                    throw new InvalidDataException($"The executable patch at 0x{p.Offset:X} is malformed.");
                if (ExeBase != null && p.End > ExeBase.Value.Size) throw new InvalidDataException($"The executable patch at 0x{p.Offset:X} lies outside u9.exe.");
                if (k > 0 && sorted[k - 1].End > p.Offset) throw new InvalidDataException($"Executable patches overlap at 0x{p.Offset:X}.");
            }
        }

        // ---- reading

        public static ModPackage Read(string path)
        {
            using var fs = File.OpenRead(path);
            return Read(fs);
        }

        public static ModPackage Read(Stream input)
        {
            ZipArchive zip;
            try { zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
            catch (InvalidDataException) { throw new InvalidDataException("Not a mod package (not a zip file)."); }
            using (zip)
            {
                var entries = zip.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), e => e);
                if (!entries.TryGetValue("mod.json", out var manifest)) throw new InvalidDataException("Not a mod package (no mod.json).");
                long total = 0;
                byte[] ReadEntry(ZipArchiveEntry e)
                {
                    if (e.Length > MaxEntry || (total += e.Length) > MaxTotal) throw new InvalidDataException("The package is too large.");
                    using var s = e.Open();
                    using var ms = new MemoryStream();
                    var buf = new byte[81920];
                    int n; long got = 0;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        if ((got += n) > e.Length || got > MaxEntry) throw new InvalidDataException("A package entry is larger than it says.");
                        ms.Write(buf, 0, n);
                    }
                    return ms.ToArray();
                }
                JsonNode j;
                try { j = JsonNode.Parse(ReadEntry(manifest)); }
                catch (JsonException ex) { throw new InvalidDataException("mod.json is not valid JSON: " + ex.Message); }
                try
                {
                    if ((int?)j["format"] != Format) throw new InvalidDataException($"This mod is format {(int?)j["format"]}; this program reads format {Format}.");
                    if ((string)j["game"] != "u9") throw new InvalidDataException($"This mod is for \"{(string)j["game"]}\", not Ultima IX.");
                    var m = new ModPackage
                    {
                        Id = (string)j["id"] ?? "", Name = (string)j["name"] ?? "", Version = (string)j["version"] ?? "",
                        Description = (string)j["description"] ?? "", MadeWith = (string)j["made_with"] ?? "",
                        Authors = j["authors"]?.AsArray().Select(a => (string)a).Where(a => a != null).ToList() ?? new List<string>(),
                    };
                    foreach (var f in j["files"]?.AsArray() ?? new JsonArray())
                    {
                        var mf = new ModFile
                        {
                            Path = (string)f["path"], Base = IdFrom(f["base"]), Target = IdFrom(f["target"]),
                            DeltaPath = (string)f["delta"]["path"],
                        };
                        if (mf.DeltaPath == null || !entries.TryGetValue(mf.DeltaPath, out var de)) throw new InvalidDataException($"{mf.Path}'s delta is missing.");
                        mf.Delta = ReadEntry(de);
                        if (FileId.Hash(mf.Delta) != ((string)f["delta"]["sha256"])?.ToLowerInvariant()) throw new InvalidDataException($"{mf.Path}'s delta is damaged (its hash does not match).");
                        m.Files.Add(mf);
                    }
                    if (j["exe"] is JsonObject exe)
                    {
                        m.ExeBase = IdFrom(exe["base"]);
                        foreach (var p in exe["patches"]?.AsArray() ?? new JsonArray())
                            m.ExePatches.Add(new ExePatch
                            {
                                Offset = (int)p["offset"], Old = Convert.FromHexString((string)p["old"]), New = Convert.FromHexString((string)p["new"]),
                                Note = (string)p["note"] ?? "",
                            });
                    }
                    m.Validate();
                    return m;
                }
                catch (Exception ex) when (ex is NullReferenceException || ex is FormatException || ex is InvalidOperationException || ex is KeyNotFoundException)
                {
                    throw new InvalidDataException("mod.json is malformed: " + ex.Message);
                }
            }
        }

        static FileId IdFrom(JsonNode n)
        {
            string sha = ((string)n["sha256"])?.ToLowerInvariant();
            long size = (long)n["size"];
            if (sha == null || sha.Length != 64 || !sha.All(Uri.IsHexDigit) || size < 0) throw new InvalidDataException("A file's hash or size is malformed.");
            return new FileId(sha, size);
        }

        /// <summary>The package's SHA-256 (of the file), for telling two copies of a mod apart.</summary>
        public static string HashOf(string path)
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
    }
}
