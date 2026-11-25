using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using U9.Formats.Combat;
using U9.Formats.Text;

namespace U9Editor.Core
{
    /// <summary>One name and what it becomes (Stays: why it does not change, or null).</summary>
    public sealed class VowelName
    {
        public string Was, Now, Stays;
    }

    /// <summary>What vowel play changes (or would change).</summary>
    public sealed class VowelPlayResult
    {
        public int Seed;
        public readonly List<VowelName> Names = new();
        public readonly List<(int Line, string Before, string After)> Edits = new();

        public string Report(bool applied)
        {
            var sb = new StringBuilder();
            sb.AppendLine((applied ? "Vowel play applied" : "Preview: vowel play") + $" (seed {Seed}).");
            sb.AppendLine();
            foreach (var n in Names.Where(n => n.Stays == null)) sb.AppendLine($"  {n.Was,-16} -> {n.Now}");
            var kept = Names.Where(n => n.Stays != null).ToList();
            if (kept.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Staying:");
                foreach (var n in kept) sb.AppendLine($"  {n.Was}: {n.Stays}");
            }
            sb.AppendLine();
            sb.AppendLine($"{Edits.Count} line{(Edits.Count == 1 ? "" : "s")} of text.flx {(applied ? "changed" : "would change")}.");
            foreach (var e in Edits.Take(12))
            {
                sb.AppendLine();
                sb.AppendLine($"  {e.Line}: {e.Before}");
                sb.AppendLine($"  {new string(' ', e.Line.ToString().Length)}  {e.After}");
            }
            if (Edits.Count > 12) sb.AppendLine($"  … and {Edits.Count - 12} more.");
            sb.AppendLine();
            sb.AppendLine("The voices still say the old names. The speaker names in runtime/NPC.FLX stay as they are: the game finds each line by them.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Vowel play: proper names get their vowels shuffled. The idea is from Merrow, the Quest 64 randomizer
    /// (https://github.com/hangedmandesign/merrow), whose "Vowel play" does it to Quest 64's monsters; this follows the
    /// Ultima Underworld editor's version of it. Each vowel becomes a random vowel (a, e, i, o, u, keeping its case; y is
    /// a consonant), so a name keeps its length, consonants and look ("Raven" -> "Rivon"). Each name gets one new spelling
    /// from the seed and the name, used in every line of static/text.flx (subtitles, replies, the journal); the name in
    /// capitals follows too. A new spelling is never a word the text already has, another name or another name's new
    /// spelling. Only the displayed text changes: the game finds lines by the English literals compiled into u9.exe and
    /// by the speakers' NPC.FLX names, neither of which is touched, so every line is still found (docs/exe-research.md,
    /// "Dialogue"); replies stay unique because every name changes the same way everywhere.
    /// </summary>
    public static class VowelPlay
    {
        const string Vowels = "aeiou";
        static readonly HashSet<string> Titles = new(StringComparer.Ordinal)
            { "The", "A", "An", "Of", "Sir", "Lord", "Lady", "King", "Queen", "Master", "Captain", "Sgt", "Generic", "Ghost", "Voice",
              "Guard", "Court", "Shrine", "Winged", "Small", "Giant", "Phase", "Ice", "Bloated", "Stone", "Arctic", "Predatory" };
        /// <summary>Names of the world that no NPC.FLX record carries.</summary>
        static readonly string[] Extra = { "Avatar", "Britannia", "Guardian", "Pagan", "Lycaeum", "Hythloth", "Covetous" };
        /// <summary>The eight mantras: spoken at the shrines; left alone in case anything compares them.</summary>
        public static readonly string[] Mantras = { "Ahm", "Mu", "Ra", "Beh", "Cah", "Summ", "Om", "Lum" };

        static readonly Regex WordRx = new(@"[A-Za-z][A-Za-z']*[A-Za-z]|[A-Za-z]", RegexOptions.CultureInvariant);

        /// <summary>Every whole word of the text, as written (a trailing 's dropped).</summary>
        public static HashSet<string> Words(IEnumerable<string> lines)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in lines)
                if (s != null && !TextTable.IsMarker(s))
                    foreach (Match m in WordRx.Matches(s)) set.Add(m.Value.EndsWith("'s") ? m.Value[..^2] : m.Value);
            return set;
        }

        static bool HasVowel(string w) => w.Any(c => Vowels.IndexOf(char.ToLowerInvariant(c)) >= 0);

        /// <summary>"LordBritish" -> "Lord", "British"; "Captain Wolford" -> "Captain", "Wolford".</summary>
        static IEnumerable<string> NameWords(string name) =>
            Regex.Matches(name ?? "", @"[A-Z][a-z']+").Select(m => m.Value.TrimEnd('\''));

        /// <summary>
        /// The names to change at first: every capitalised word of the NPC.FLX names (titles and generic words left out)
        /// that has a vowel, is used in the text and never appears there in lower case ("Raven" does not, "Bat" might);
        /// then the world's other names the text uses (Avatar, Britannia, Guardian…).
        /// </summary>
        public static List<string> DefaultNames(IEnumerable<string> lines, IEnumerable<string> npcNames)
        {
            var words = Words(lines);
            var list = new List<string>();
            void Consider(string t)
            {
                if (t.Length < 3 || !HasVowel(t) || Titles.Contains(t) || !words.Contains(t) || words.Contains(t.ToLowerInvariant())) return;
                if (!list.Contains(t)) list.Add(t);
            }
            foreach (var n in npcNames) foreach (var t in NameWords(n)) Consider(t);
            foreach (var t in Extra) Consider(t);
            return list;
        }

        /// <summary>A stable number for a name (FNV-1a), so a name's new spelling depends only on the seed and the name.</summary>
        static int Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return (int)(h & 0x7FFFFFFF);
            }
        }

        /// <summary>The name with each vowel drawn again (case kept; with keepFirst, a first vowel stays).</summary>
        public static string Shuffle(string name, Random rng, bool keepFirst)
        {
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (Vowels.IndexOf(char.ToLowerInvariant(c)) < 0 || keepFirst && i == 0) { sb.Append(c); continue; }
                char v = Vowels[rng.Next(Vowels.Length)];
                sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(v) : v);
            }
            return sb.ToString();
        }

        /// <summary>Each name's new spelling: drawn again (up to 64 times) while it is the same, a word of the text, another name or another new spelling.</summary>
        public static List<VowelName> Plan(IEnumerable<string> names, HashSet<string> words, int seed, bool keepFirst)
        {
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var all = new HashSet<string>(list.Select(n => n.ToUpperInvariant()), StringComparer.Ordinal);
            var vocabulary = new HashSet<string>(words.Select(w => w.ToUpperInvariant()), StringComparer.Ordinal);
            var protect = new HashSet<string>(Mantras.Select(m => m.ToUpperInvariant()), StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var res = new List<VowelName>();
            foreach (var n in list)
            {
                var v = new VowelName { Was = n, Now = n };
                res.Add(v);
                if (protect.Contains(n.ToUpperInvariant())) { v.Stays = "a mantra"; continue; }
                if (!HasVowel(keepFirst ? n[1..] : n)) { v.Stays = "no vowel to change"; continue; }
                var rng = new Random(Hash(n) ^ seed);
                string got = null;
                for (int tries = 0; tries < 64 && got == null; tries++)
                {
                    string c = Shuffle(n, rng, keepFirst), u = c.ToUpperInvariant();
                    if (c == n || all.Contains(u) || vocabulary.Contains(u) || taken.Contains(u) || protect.Contains(u)) continue;
                    got = c;
                }
                if (got == null) { v.Stays = "every new spelling tried was already a word or a name"; continue; }
                v.Now = got;
                taken.Add(got.ToUpperInvariant());
            }
            return res;
        }

        /// <summary>The words to look for and what each becomes (the name and its capitals).</summary>
        public static Dictionary<string, string> Swaps(IEnumerable<VowelName> names)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in names.Where(n => n.Stays == null))
            {
                d[n.Was] = n.Now;
                string up = n.Was.ToUpperInvariant();
                if (up != n.Was && !d.ContainsKey(up)) d[up] = n.Now.ToUpperInvariant();
            }
            return d;
        }

        /// <summary>A regex finding any of the words as a whole word (case-sensitive: "Raven" changes, "raven" does not).</summary>
        public static Regex Finder(IEnumerable<string> words)
        {
            var alts = words.Where(w => !string.IsNullOrEmpty(w)).Distinct().OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal)
                .Select(Regex.Escape).ToList();
            return alts.Count == 0 ? null : new Regex(@"(?<![A-Za-z])(" + string.Join("|", alts) + @")(?![A-Za-z])", RegexOptions.CultureInvariant);
        }

        /// <summary>Works out every change on the lines as they are now. Nothing is changed.</summary>
        public static VowelPlayResult Preview(TextTable text, IEnumerable<string> names, int seed, bool keepFirst)
        {
            var r = new VowelPlayResult { Seed = seed };
            r.Names.AddRange(Plan(names, Words(text.Lines), seed, keepFirst));
            var swap = Swaps(r.Names);
            var finder = Finder(swap.Keys);
            if (finder == null) return r;
            for (int i = 0; i < text.Count; i++)
            {
                string s = text[i];
                if (s == null || TextTable.IsMarker(s)) continue;
                string t = finder.Replace(s, m => swap.TryGetValue(m.Value, out var now) ? now : m.Value);
                if (t != s) r.Edits.Add((i, s, t));
            }
            return r;
        }

        /// <summary>The NPC.FLX names, for <see cref="DefaultNames"/>.</summary>
        public static IEnumerable<string> NpcNames(NpcTable npcs) => npcs.Records().Select(n => n.Name).Where(n => n.Length > 0);
    }
}
