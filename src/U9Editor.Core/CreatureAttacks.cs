using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Combat;

namespace U9Editor.Core
{
    /// <summary>
    /// Which attacks a creature makes, for the combat balance. A choice is a natural-attack row ("n:6"), a weapon type
    /// ("w:5388"), an unarmed blow worked out from strength ("u:0" punch, "u:1" kick, "u:2" headbutt), "none" (never
    /// attacks) or "" (not known). The defaults follow the creature's kind, which picks its AI class in the game
    /// (FUN_004F5DC0; analysis/item-stats/attacks.md and attacks-audit.md).
    /// </summary>
    public static class CreatureAttacks
    {
        /// <summary>Kind → choices: the class's natural rows, or the weapon it creates when its map contents hold none of the class it asks for.</summary>
        public static readonly Dictionary<int, string[]> DefaultsByKind = new()
        {
            [1] = new[] { "n:26" }, [2] = new[] { "n:27" }, [3] = new[] { "n:6" }, [4] = new[] { "n:10", "n:11" }, [5] = new[] { "n:20" },
            [8] = new[] { "n:7" }, [47] = new[] { "n:7" }, [48] = new[] { "n:7" }, [12] = new[] { "n:15" }, [13] = new[] { "n:17" },
            [14] = new[] { "n:21" }, [17] = new[] { "n:19" }, [19] = new[] { "n:14" }, [20] = new[] { "n:5" }, [23] = new[] { "n:12" },
            [24] = new[] { "n:12" }, [52] = new[] { "n:24" }, [25] = new[] { "n:6" }, [26] = new[] { "n:6" }, [27] = new[] { "n:22" }, [44] = new[] { "n:23" },
            [28] = new[] { "n:4" }, [29] = new[] { "n:4" }, [30] = new[] { "n:4" }, [31] = new[] { "n:4" }, [51] = new[] { "n:4" }, [45] = new[] { "n:4" },
            [32] = new[] { "n:9" }, [55] = new[] { "n:16" },
            [36] = new[] { "w:4789" }, [22] = new[] { "w:1042" }, [42] = new[] { "w:5388" }, [57] = new[] { "w:5388" }, [61] = new[] { "w:5388" },
            [62] = new[] { "w:1042" }, [15] = new[] { "w:2640" }, [41] = new[] { "w:4940" }, [9] = new[] { "w:5389" }, [40] = new[] { "w:3692" },
            [38] = new[] { "w:4836" },
            [0] = new[] { "u:0", "u:1", "u:2" }, [34] = new[] { "u:0", "u:1", "u:2" }, [39] = new[] { "u:0", "u:1", "u:2" },
            [49] = new[] { "u:0", "u:1", "u:2" }, [56] = new[] { "u:0", "u:1", "u:2" }, [63] = new[] { "u:0", "u:1", "u:2" },
            [-1] = new[] { "none" }, [16] = new[] { "none" }, [18] = new[] { "none" }, [35] = new[] { "none" }, [37] = new[] { "none" },
            [21] = new[] { "none" }, [33] = new[] { "none" }, [50] = new[] { "none" }, [58] = new[] { "none" }, [59] = new[] { "none" }, [60] = new[] { "none" },
            // 6 Slasher Demon, 7 dragons, 10 winged gargoyles, 11 Zhah, 46 Liche: spells and effect objects, damage not traced.
        };

        public static string[] DefaultsOf(NpcRecord n) => DefaultsByKind.TryGetValue(n.Kind, out var c) ? c : Array.Empty<string>();

        static readonly string[] Blows = { "punch", "kick", "headbutt" };

        /// <summary>The game's unarmed damage (FUN_004F82F0): 2·Str+2, 3·(Str+1), 4·Str+4 with the attacker's +0x28.</summary>
        public static int UnarmedDamage(int row, int strength) => row switch { 0 => 2 * strength + 2, 1 => 3 * (strength + 1), _ => 4 * strength + 4 };

        /// <summary>The attack a choice stands for; null for "none", "" and anything unknown.</summary>
        public static Attack? Resolve(CombatTables t, string choice, Func<int, string> typeNames, NpcRecord? attacker = null)
        {
            if (string.IsNullOrEmpty(choice) || choice.Length < 3 || choice[1] != ':' || !int.TryParse(choice[2..], out int v)) return null;
            if (choice[0] == 'n')
            {
                if (v < 0 || v >= CombatTables.NaturalCount) return null;
                var a = t.NaturalAttacks()[v];
                return new Attack($"natural {v} ({a.Bone.ToLowerInvariant()}, {CombatMath.FlagNames(a.Flags)})", a.Min, a.Max, a.Flags, a.Special);
            }
            if (choice[0] == 'u' && v >= 0 && v <= 2)
            {
                int str = attacker?.Strength ?? 0;
                if (str < 0 || str > 100) str = 0;
                int d = UnarmedDamage(v, str);
                var row = t.NaturalAttacks()[v];
                return new Attack($"{Blows[v]} (unarmed, strength {str})", d, d, row.Flags, 0);
            }
            if (choice[0] == 'w' && t.WeaponOf(v) is Weapon w && !w.IsShield && !w.IsLight)
            {
                var m = t.AttackModeOf(w.Type, w.Class, 0);
                string name = typeNames?.Invoke(w.Type) ?? "type " + w.Type;
                return new Attack($"{name} ({CombatMath.FlagNames(m?.Flags ?? 0)})", w.Min, w.Max, m?.Flags ?? 0, m?.Special ?? 0);
            }
            return null;
        }

        public static string Describe(string choice) => choice switch { "none" => "never attacks", "" or null => "attack not known", _ => choice };

        /// <summary>Every choice there is: never attacks, unarmed blows, the natural rows, then the weapons (not shields or lights).</summary>
        public static List<(string Choice, string Label)> All(CombatTables t, Func<int, string> typeNames)
        {
            var list = new List<(string, string)> { ("none", "never attacks") };
            for (int i = 0; i < 3; i++) list.Add(($"u:{i}", $"unarmed {Blows[i]} (from its strength)"));
            foreach (var a in t.NaturalAttacks())
                if (a.Max > 0 && a.Index > 2) list.Add(($"n:{a.Index}", $"natural {a.Index}: {a.Min}-{a.Max} {CombatMath.FlagNames(a.Flags)} ({a.Bone.ToLowerInvariant()})"));
            var seen = new HashSet<int>();
            foreach (var w in t.Weapons())
                if (!w.IsShield && !w.IsLight && w.Max > 0 && seen.Add(w.Type))
                    list.Add(($"w:{w.Type}", $"{typeNames?.Invoke(w.Type) ?? "type " + w.Type}: {w.Min}-{w.Max}"));
            return list;
        }
    }
}
