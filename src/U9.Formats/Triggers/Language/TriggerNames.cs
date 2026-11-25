using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using U9.Formats.Models;
using U9.Formats.Objects;

namespace U9.Formats.Triggers.Language
{
    /// <summary>
    /// The names the trigger language uses in place of numbers: object classes (PascalCase), constants
    /// (UPPER_SNAKE) and globals, plus label names per script. Built from the game's type names
    /// (static/TYPENAME.FLX), the classes in static/TYPES.DAT, the names files, in that order (later wins).
    /// <para>A names file has one entry a line; '#' starts a comment:
    /// <c>class 1111 TrgZones</c>, <c>global 12 MET_LORD_BRITISH</c>, <c>flag 0x100 DOOR_OPEN</c> (a flag bit as op
    /// B writes it), <c>label 7765 1 done</c> (script, label number, name).</para>
    /// </summary>
    public sealed class TriggerNames
    {
        readonly Dictionary<int, string> classNames = new Dictionary<int, string>();
        readonly Dictionary<string, int> classByName = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<int, string> globalNames = new Dictionary<int, string>();
        readonly Dictionary<string, int> globalByName = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<int, string> flagNames = new Dictionary<int, string>();
        readonly Dictionary<string, int> flagByName = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<(int Slot, int Number), string> labelNames = new Dictionary<(int, int), string>();
        int[] usecode = Array.Empty<int>();

        /// <summary>The B bits of ops 06, 07, 08, 29 and 3B that map to an object flag, and the flag each sets (0x7930D8 to 0x793108).</summary>
        public static readonly (int Bit, int ObjectFlag)[] FlagMap =
        {
            (0x1, 0x2), (0x2, 0x40), (0x4, 0x8), (0x8, 0x100), (0x20, 0x400), (0x100, 0x4000),
            (0x200, 0x8000), (0x400, 0x10000), (0x800, 0x20000), (0x1000, 0x40000),
        };

        /// <summary>The movies of op 37, by number (the table at 0x795098).</summary>
        public static readonly string[] Movies =
        {
            "MOVIE_COLUMN", "MOVIE_LOGO", "MOVIE_INTRO", "MOVIE_AMBUSH", "MOVIE_SAMHAYNE", "MOVIE_RAVEN", "MOVIE_ARMAGEDDON",
            "MOVIE_GOOD_ENDGAME", "MOVIE_MAELSTROM", "MOVIE_MOONGATE", "MOVIE_PRISON", "MOVIE_PYROS", "MOVIE_SKARA_BRAE",
        };

        /// <summary>The fixed constants of the statements that take bit sets or choices: name to value, per group.</summary>
        public static readonly Dictionary<string, (string Name, int Value)[]> Groups = new Dictionary<string, (string, int)[]>
        {
            ["move"] = new[] { ("GLIDE", 0x8000), ("BACK", 0x4000), ("THEN_SCRIPT1", 0x2000), ("THEN_SCRIPT2", 0x1000), ("STRAIGHT", 0x0800) },
            ["options"] = new[] { ("SELF", 1), ("SKIPPED", 2), ("UNLOADED", 4) },
            ["script"] = new[] { ("OO1", 0), ("OO2", 1), ("SF1", 2), ("SF2", 3) },
            ["movie"] = Movies.Select((m, i) => (m, i)).ToArray(),
        };

        /// <summary>The type's usecode class (TYPES.DAT +4), or the type itself when out of the table.</summary>
        public int ClassOf(int type) => (uint)type < (uint)usecode.Length ? usecode[type] : type;

        /// <summary>Whether the number is a class (a type whose usecode is itself).</summary>
        public bool IsClass(int number) => (uint)number < (uint)usecode.Length && usecode[number] == number;

        /// <summary>The types of a class.</summary>
        public IEnumerable<int> TypesOf(int cls)
        {
            for (int t = 0; t < usecode.Length; t++) if (usecode[t] == cls) yield return t;
        }

        public string ClassName(int cls) => classNames.TryGetValue(cls, out var s) ? s : null;
        public bool TryClass(string name, out int cls) => classByName.TryGetValue(name, out cls);
        public IEnumerable<KeyValuePair<int, string>> Classes => classNames;

        public string GlobalName(int n) => globalNames.TryGetValue(n, out var s) ? s : null;
        public bool TryGlobal(string name, out int n) => globalByName.TryGetValue(name, out n);
        public IEnumerable<KeyValuePair<int, string>> Globals => globalNames;

        /// <summary>The name of a flag bit (B as ops write it); defaults are FLAG_ and the object flag in hex.</summary>
        public string FlagName(int bit) => flagNames.TryGetValue(bit, out var s) ? s : null;
        public bool TryFlag(string name, out int bit) => flagByName.TryGetValue(name, out bit);
        public IEnumerable<KeyValuePair<int, string>> Flags => flagNames;

        public string LabelName(int slot, int number) => labelNames.TryGetValue((slot, number), out var s) ? s : null;
        public IEnumerable<KeyValuePair<int, string>> LabelsOf(int slot) =>
            labelNames.Where(k => k.Key.Slot == slot).Select(k => new KeyValuePair<int, string>(k.Key.Number, k.Value));

        public TriggerNames()
        {
            foreach (var (bit, flag) in FlagMap) SetFlag(bit, "FLAG_" + flag.ToString("X", CultureInfo.InvariantCulture));
        }

        /// <summary>Names from the game's tables and the names files (missing files are skipped).</summary>
        public static TriggerNames Build(TypeNames typeNames, TypeTable types, params string[] namesFiles)
        {
            var n = new TriggerNames();
            if (types != null)
            {
                n.usecode = new int[types.Count];
                for (int t = 0; t < types.Count; t++) n.usecode[t] = types[t].Usecode;
            }
            if (typeNames != null)
            {
                // a class takes its lead type's name; names two classes would share get the number
                var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (int t = 0; t < n.usecode.Length; t++)
                {
                    if (!n.IsClass(t) || string.IsNullOrWhiteSpace(typeNames[t])) continue;
                    string p = Pascal(typeNames[t]);
                    if (p == null) continue;
                    if (!byName.TryGetValue(p, out var l)) byName[p] = l = new List<int>();
                    l.Add(t);
                }
                foreach (var kv in byName)
                    foreach (int t in kv.Value) n.SetClass(t, kv.Value.Count == 1 ? kv.Key : kv.Key + "_" + t);
            }
            foreach (string f in namesFiles ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(f) && File.Exists(f)) n.Read(File.ReadAllLines(f), f);
            return n;
        }

        /// <summary>A game name in PascalCase: "rune of honesty" becomes RuneOfHonesty; null when nothing is left.</summary>
        public static string Pascal(string s)
        {
            var sb = new StringBuilder();
            bool up = true;
            foreach (char c in s)
            {
                if (c < 128 && char.IsLetterOrDigit(c)) { sb.Append(up ? char.ToUpperInvariant(c) : c); up = false; }
                else if (c != '\'' && c != '’') up = true;      // Altara's Sceptre: AltarasSceptre
            }
            if (sb.Length == 0) return null;
            if (char.IsDigit(sb[0])) sb.Insert(0, 'T');
            return sb.ToString();
        }

        /// <summary>Reads names-file lines; unknown or malformed lines are reported in <see cref="Problems"/>.</summary>
        public void Read(IEnumerable<string> lines, string source = "names")
        {
            int n = 0;
            foreach (string raw in lines)
            {
                n++;
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                try
                {
                    switch (p[0])
                    {
                        case "class" when p.Length == 3: SetClass(Int(p[1]), p[2]); break;
                        case "global" when p.Length == 3: SetGlobal(Int(p[1]), p[2]); break;
                        case "flag" when p.Length == 3: SetFlag(Int(p[1]), p[2]); break;
                        case "label" when p.Length == 4: SetLabel(Int(p[1]), Int(p[2]), p[3]); break;
                        default: throw new FormatException("expected class, global, flag or label");
                    }
                }
                catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException)
                {
                    Problems.Add($"{source} line {n}: {e.Message}");
                }
            }
        }

        /// <summary>Lines of the names files that could not be read.</summary>
        public readonly List<string> Problems = new List<string>();

        static int Int(string s) =>
            s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(s, CultureInfo.InvariantCulture);

        static bool IsName(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_'));

        public void SetClass(int cls, string name)
        {
            if (!IsName(name) || !char.IsUpper(name[0]) || name.All(c => !char.IsLower(c))) throw new ArgumentException($"class name '{name}' must be PascalCase");
            if (classNames.TryGetValue(cls, out var old)) classByName.Remove(old);
            if (classByName.TryGetValue(name, out int other)) classNames.Remove(other);
            classNames[cls] = name; classByName[name] = cls;
        }

        public void SetGlobal(int n, string name)
        {
            if (!IsName(name) || name.Any(char.IsLower)) throw new ArgumentException($"global name '{name}' must be UPPER_SNAKE");
            if (n < 0 || n > 0x1FF) throw new ArgumentException("globals run from 0 to 511");
            if (globalNames.TryGetValue(n, out var old)) globalByName.Remove(old);
            if (globalByName.TryGetValue(name, out int other)) globalNames.Remove(other);
            globalNames[n] = name; globalByName[name] = n;
        }

        public void SetFlag(int bit, string name)
        {
            if (!IsName(name) || name.Any(char.IsLower)) throw new ArgumentException($"flag name '{name}' must be UPPER_SNAKE");
            if (bit <= 0 || (bit & (bit - 1)) != 0 || bit > 0x1000) throw new ArgumentException("a flag is one bit of B, 0x1 to 0x1000");
            if (flagNames.TryGetValue(bit, out var old)) flagByName.Remove(old);
            if (flagByName.TryGetValue(name, out int other)) flagNames.Remove(other);
            flagNames[bit] = name; flagByName[name] = bit;
        }

        public void SetLabel(int slot, int number, string name)
        {
            if (!IsName(name) || name.Any(char.IsUpper)) throw new ArgumentException($"label name '{name}' must be lower_snake");
            foreach (var k in labelNames.Where(k => k.Key.Slot == slot && k.Value == name).Select(k => k.Key).ToList()) labelNames.Remove(k);
            labelNames[(slot, number)] = name;
        }

        public void ClearLabels(int slot)
        {
            foreach (var k in labelNames.Keys.Where(k => k.Slot == slot).ToList()) labelNames.Remove(k);
        }

        /// <summary>The user's names as names-file lines: everything that differs from <paramref name="defaults"/>.</summary>
        public IEnumerable<string> Lines(TriggerNames defaults)
        {
            foreach (var kv in classNames.OrderBy(k => k.Key))
                if (defaults?.ClassName(kv.Key) != kv.Value) yield return $"class {kv.Key} {kv.Value}";
            foreach (var kv in globalNames.OrderBy(k => k.Key))
                if (defaults?.GlobalName(kv.Key) != kv.Value) yield return $"global {kv.Key} {kv.Value}";
            foreach (var kv in flagNames.OrderBy(k => k.Key))
                if (defaults?.FlagName(kv.Key) != kv.Value) yield return $"flag 0x{kv.Key:X} {kv.Value}";
            foreach (var kv in labelNames.OrderBy(k => k.Key.Slot).ThenBy(k => k.Key.Number))
                if (defaults?.LabelName(kv.Key.Slot, kv.Key.Number) != kv.Value) yield return $"label {kv.Key.Slot} {kv.Key.Number} {kv.Value}";
        }
    }
}
