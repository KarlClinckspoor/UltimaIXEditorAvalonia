using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace U9.Mods
{
    /// <summary>One mod in a catalog (docs/mod-format.md, "Catalog").</summary>
    public sealed class CatalogEntry
    {
        public string Id = "", Name = "", Version = "", Description = "", Url = "", Sha256 = "", Homepage = "";
        public List<string> Authors = new List<string>();
        public long Size;
        public override string ToString() => $"{Name} {Version}";
    }

    /// <summary>The mods' library (imported packages), the catalog, and U9GL's releases.</summary>
    public sealed class ModLibrary
    {
        public string Home { get; }
        public string Folder => Path.Combine(Home, "library");
        static readonly HttpClient http = MakeClient();

        public ModLibrary(string home = null) { Home = home ?? GameInstall.DefaultHome; }

        static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("U9ModManager/1.0");
            return c;
        }

        // ---- library

        public sealed class Item
        {
            public string Path;
            public ModPackage Mod;
            public string Problem;
            public override string ToString() => Mod != null ? Mod.ToString() : System.IO.Path.GetFileName(Path) + " (" + Problem + ")";
        }

        /// <summary>Every package in the library, read and checked (a damaged one says why).</summary>
        public List<Item> Items()
        {
            var list = new List<Item>();
            if (!Directory.Exists(Folder)) return list;
            foreach (var f in Directory.EnumerateFiles(Folder, "*" + ModPackage.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try { list.Add(new Item { Path = f, Mod = ModPackage.Read(f) }); }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException) { list.Add(new Item { Path = f, Problem = ex.Message }); }
            }
            return list;
        }

        /// <summary>Copies a package into the library after reading it whole; replaces an older copy of the same mod.</summary>
        public Item Import(string package)
        {
            var mod = ModPackage.Read(package);
            Directory.CreateDirectory(Folder);
            foreach (var old in Items().Where(i => i.Mod?.Id == mod.Id)) File.Delete(old.Path);
            string dest = Path.Combine(Folder, $"{mod.Id}-{Safe(mod.Version)}{ModPackage.Extension}");
            File.Copy(package, dest + ".part", true);
            File.Move(dest + ".part", dest, true);
            return new Item { Path = dest, Mod = mod };
        }

        public void Remove(string id)
        {
            foreach (var i in Items().Where(i => i.Mod?.Id == id)) File.Delete(i.Path);
        }

        static string Safe(string s) => new string((s ?? "").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());

        // ---- catalog

        public static List<CatalogEntry> ParseCatalog(string json)
        {
            var j = JsonNode.Parse(json);
            if ((int?)j["format"] != 1) throw new InvalidDataException("Not a U9 mod catalog (format 1).");
            var list = new List<CatalogEntry>();
            foreach (var m in j["mods"]?.AsArray() ?? new JsonArray())
            {
                var e = new CatalogEntry
                {
                    Id = (string)m["id"] ?? "", Name = (string)m["name"] ?? "", Version = (string)m["version"] ?? "",
                    Description = (string)m["description"] ?? "", Url = (string)m["url"] ?? "",
                    Sha256 = ((string)m["sha256"] ?? "").ToLowerInvariant(), Homepage = (string)m["homepage"] ?? "",
                    Size = (long?)m["size"] ?? 0,
                    Authors = m["authors"]?.AsArray().Select(a => (string)a).Where(a => a != null).ToList() ?? new List<string>(),
                };
                if (!ModPackage.IsValidId(e.Id) || e.Sha256.Length != 64 || !Uri.TryCreate(e.Url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) continue;
                list.Add(e);
            }
            return list;
        }

        public async Task<List<CatalogEntry>> FetchCatalog(string url, CancellationToken ct = default) =>
            ParseCatalog(await http.GetStringAsync(url, ct));

        /// <summary>Downloads a catalog entry, checks its SHA-256 and reads it as a package before importing it.</summary>
        public async Task<Item> Download(CatalogEntry e, IProgress<double> progress = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Home);
            string temp = Path.Combine(Home, "download-" + Guid.NewGuid().ToString("N") + ModPackage.Extension);
            try
            {
                string hash = await DownloadTo(e.Url, temp, 1L << 30, progress, ct);
                if (hash != e.Sha256) throw new InvalidDataException($"{e.Name}: the download is not the file the catalog lists (SHA-256 differs).");
                var item = Import(temp);
                if (item.Mod.Id != e.Id) { File.Delete(item.Path); throw new InvalidDataException($"The download is mod \"{item.Mod.Id}\", not \"{e.Id}\" as the catalog says."); }
                return item;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static async Task<string> DownloadTo(string url, string path, long max, IProgress<double> progress, CancellationToken ct)
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? length = resp.Content.Headers.ContentLength;
            if (length > max) throw new InvalidDataException("The download is too large.");
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = File.Create(path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[81920];
            long got = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                if ((got += n) > max) throw new InvalidDataException("The download is too large.");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.AppendData(buf, 0, n);
                if (length > 0) progress?.Report(got / (double)length.Value);
            }
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        // ---- U9GL

        public const string U9glRepo = "theGreyWanderer-uc/tgwUltima9GL";

        public sealed class U9glRelease
        {
            public string Version, ZipUrl, ShaUrl, ZipName, Notes;
        }

        /// <summary>
        /// U9GL's latest release on GitHub: its zip (u9gl-&lt;version&gt;.zip, which brings its own .NET; with
        /// <paramref name="noRuntime"/> the smaller -noruntime one, which needs .NET installed) and where its SHA-256 is
        /// listed (a .sha256 beside it, or the release's SHA256SUMS, as 0.6.1 has).
        /// </summary>
        public async Task<U9glRelease> LatestU9gl(bool noRuntime = false, CancellationToken ct = default)
        {
            var j = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{U9glRepo}/releases/latest", ct));
            var assets = j["assets"]?.AsArray() ?? new JsonArray();
            static string Name(JsonNode a) => (string)a["name"] ?? "";
            var zip = assets.FirstOrDefault(a => Name(a).StartsWith("u9gl-", StringComparison.OrdinalIgnoreCase) && Name(a).EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                                 Name(a).Contains("-noruntime", StringComparison.OrdinalIgnoreCase) == noRuntime);
            if (zip == null) throw new InvalidDataException($"U9GL's latest release has no u9gl-<version>{(noRuntime ? "-noruntime" : "")}.zip.");
            string zipName = Name(zip);
            var sha = assets.FirstOrDefault(a => string.Equals(Name(a), zipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                      ?? assets.FirstOrDefault(a => string.Equals(Name(a), "SHA256SUMS", StringComparison.OrdinalIgnoreCase));
            if (sha == null) throw new InvalidDataException($"U9GL's latest release lists no SHA-256 for {zipName} to check the download against.");
            return new U9glRelease
            {
                Version = (string)j["tag_name"], ZipName = zipName, Notes = (string)j["body"] ?? "",
                ZipUrl = (string)zip["browser_download_url"], ShaUrl = (string)sha["browser_download_url"],
            };
        }

        /// <summary>The SHA-256 a checksum file gives for a file name: "hash  name" or "hash *name" lines, or a lone hash.</summary>
        public static string ShaFor(string sums, string fileName)
        {
            var lines = sums.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            foreach (var l in lines)
            {
                var parts = l.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && string.Equals(parts[1].Trim().TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase)) return parts[0].ToLowerInvariant();
            }
            if (lines.Count == 1 && lines[0].Length == 64 && lines[0].All(Uri.IsHexDigit)) return lines[0].ToLowerInvariant();
            return null;
        }

        /// <summary>The U9GL version unpacked in a game folder (U9GL.exe's version), or null when there is none.</summary>
        public static string InstalledU9gl(string gameRoot)
        {
            string exe = Path.Combine(gameRoot, "u9gl", "U9GL.exe");
            if (!File.Exists(exe)) return null;
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            string text = v.ProductVersion ?? v.FileVersion;
            return string.IsNullOrWhiteSpace(text) ? "unknown version" : text.Split('+')[0];
        }

        /// <summary>Whether U9GL's loader (dsound.dll) is beside u9.exe, i.e. its launcher has installed it.</summary>
        public static bool U9glLoaderInstalled(string gameRoot) => File.Exists(Path.Combine(gameRoot, "dsound.dll"));

        /// <summary>
        /// Downloads U9GL's release zip, checks it against the SHA-256 its release lists and unpacks its u9gl\ folder into the game folder
        /// (nothing outside it). Installing (dsound.dll) is left to U9GL's own launcher, which keeps its backups.
        /// Returns the path of U9GL.exe.
        /// </summary>
        public async Task<string> DownloadU9gl(U9glRelease rel, string gameRoot, IProgress<double> progress = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Home);
            string temp = Path.Combine(Home, rel.ZipName + ".part");
            try
            {
                string expected = ShaFor(await http.GetStringAsync(rel.ShaUrl, ct), rel.ZipName)
                                  ?? throw new InvalidDataException($"The release's checksums do not list {rel.ZipName}.");
                string got = await DownloadTo(rel.ZipUrl, temp, 512L << 20, progress, ct);
                if (got != expected) throw new InvalidDataException("The U9GL download does not match the SHA-256 its release lists.");
                string root = Path.GetFullPath(gameRoot);
                using (var zip = ZipFile.OpenRead(temp))
                {
                    var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("u9gl/", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (entries.Count == 0) throw new InvalidDataException("The U9GL zip has no u9gl folder.");
                    foreach (var e in entries)
                    {
                        string dest = Path.GetFullPath(Path.Combine(root, e.FullName));
                        if (!dest.StartsWith(Path.Combine(root, "u9gl") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(dest.TrimEnd(Path.DirectorySeparatorChar), Path.Combine(root, "u9gl"), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The U9GL zip points outside its folder.");
                        if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                }
                return Path.Combine(root, "u9gl", "U9GL.exe");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
