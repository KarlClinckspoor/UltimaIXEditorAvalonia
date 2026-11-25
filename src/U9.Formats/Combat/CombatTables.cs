using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace U9.Formats.Combat
{
    /// <summary>How a number is stored in u9.exe.</summary>
    public enum StatKind { I32, U8, F32 }

    /// <summary>One editable number in u9.exe: its file offset, how it is stored, and what it is (for patch notes).</summary>
    public readonly record struct ExeStat(int Offset, StatKind Kind, string Label)
    {
        public int Size => Kind == StatKind.U8 ? 1 : 4;
    }

    /// <summary>A weapon, shield or light of the weapon tables (T1: 52 records, T2: 13 more, looked up after T1).</summary>
    public readonly record struct Weapon(int Index, int Offset, int Type, int Class, int Min, int Max, float Reach, float Radius, int Magic)
    {
        public bool IsShield => Class == 5;
        public bool IsLight => Class == 6;
        public bool IsBow => Class == 4;
    }

    /// <summary>A row of the attack-mode table: the damage types of a weapon class's (key &lt; 16) or a weapon type's attack.</summary>
    public readonly record struct AttackMode(int Index, int Offset, int Key, int Mode, bool Blockable, int Flags, int Special);

    /// <summary>A piece of the Avatar's armour: slot (0 helmet .. 5 boots), level and the defence range rolled per hit.</summary>
    public readonly record struct ArmourPiece(int Index, int Offset, int Type, int Slot, int Level, int Min, int Max);

    /// <summary>A creature's built-in attack (bite, claw, breath): damage range and types.</summary>
    public readonly record struct NaturalAttack(int Index, int Offset, int Min, int Max, float Reach, float Radius, int Flags, int Special, string Bone);

    /// <summary>
    /// The combat tables of the GOG u9.exe (analysis/item-stats/report.md, audited): weapons, attack modes, armour,
    /// natural attacks, and the damage multipliers per creature kind, which start-up code at VA 0x44A040 writes into
    /// memory (instruction constants, read here by walking that code). Works on a copy of the image: setters change the
    /// copy, <see cref="Changes"/> lists what differs from the original as notes and bytes.
    /// </summary>
    public sealed class CombatTables
    {
        // File offsets (GOG u9.exe; .data file offset = VA - 0x401A00, .text = VA - 0x400C00).
        public const int WeaponT1 = 0x39D868, WeaponT1Count = 52, WeaponT2 = 0x39DE18, WeaponT2Count = 13, WeaponSize = 28;
        public const int AttackModes = 0x39E000, AttackModeCountAt = 0x39E424, AttackModeSize = 20;
        public const int Armour = 0x37F270, ArmourCount = 72, ArmourSize = 16;
        public const int Natural = 0x383D38, NaturalCount = 28, NaturalSize = 24, NaturalBones = 0x383FD8;
        public const int Builder = 0x49440, BuilderEnd = 0x4A904;     // VA 0x44A040 .. 0x44B504
        public const uint MultiplierVa = 0x84E598, HelperVa = 0x449EB0, TextDelta = 0x400C00, DataDelta = 0x401A00;
        public const int Kinds = 64, Bits = 11;

        /// <summary>The damage-type bits (attack-mode flags) the multipliers follow, by bit. Three are not known.</summary>
        public static readonly string[] BitNames = { "slash", "blunt", "thrust", "arrow", "bit 4", "bit 5", "fire", "cold", "magic", "bit 9", "lightning" };
        public static readonly string[] ClassNames = { "fist", "one-handed", "two-handed", "staff", "bow", "shield", "light" };
        public static readonly string[] SlotNames = { "Helmet", "Gauntlets", "Leggings", "Arms", "Chest", "Boots" };

        readonly byte[] original;
        readonly byte[] image;
        int[] sites;          // per kind*11+bit: file offset of the constant, -1 when it cannot change in place
        float[] multipliers;
        readonly float[] originalMultipliers;
        int tableAt = -1;     // file offset of the multiplier table once the builder is rewritten as one

        CombatTables(byte[] exe)
        {
            original = exe;
            image = (byte[])exe.Clone();
            string why = Check(exe);
            if (why != null) throw new InvalidDataException(why);
            ReadMultipliers();
            originalMultipliers = (float[])multipliers.Clone();
        }

        /// <summary>The tables of u9.exe's bytes. Throws InvalidDataException for another build than the GOG one.</summary>
        public static CombatTables Read(byte[] exe) => new CombatTables(exe);
        public static CombatTables Load(string exePath) => Read(File.ReadAllBytes(exePath));

        /// <summary>Why these bytes are not the u9.exe whose tables are known (null when they are).</summary>
        public static string Check(byte[] exe)
        {
            if (exe.Length != 4_593_152) return $"u9.exe is {exe.Length:N0} bytes: the combat tables are known only for the GOG build (4,593,152 bytes).";
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(o));
            if (I(WeaponT1) != 1042 || I(WeaponT1 + 4) != 1 || I(WeaponT2) != 3780 || I(AttackModeCountAt) != 53 || I(Armour + ArmourSize) != 4634)
                return "u9.exe's weapon, attack or armour tables are not where the GOG build has them.";
            bool code = exe[Builder] == 0xC7 && exe[Builder + 1] == 0x05 && BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(Builder + 2)) == MultiplierVa;
            if (!code && !IsTableStub(exe)) return "u9.exe's damage-multiplier code is not the GOG build's.";
            return null;
        }

        /// <summary>The working image (original plus edits).</summary>
        public byte[] Image => image;
        public byte[] Original => original;

        int I32(int o) => BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(o));
        float F32(int o) => BinaryPrimitives.ReadSingleLittleEndian(image.AsSpan(o));

        // ---- reading ----

        public static int WeaponOffset(int index) => index < WeaponT1Count ? WeaponT1 + WeaponSize * index : WeaponT2 + WeaponSize * (index - WeaponT1Count);
        public int WeaponCount => WeaponT1Count + WeaponT2Count;

        public Weapon WeaponAt(int index)
        {
            int o = WeaponOffset(index);
            return new Weapon(index, o, I32(o) & 0xFFFF, I32(o + 4), I32(o + 8), I32(o + 12), F32(o + 16), F32(o + 20), I32(o + 24));
        }

        public List<Weapon> Weapons() => Enumerable.Range(0, WeaponCount).Select(WeaponAt).ToList();

        /// <summary>The record the game uses for a type (T1 first, then T2), or null.</summary>
        public Weapon? WeaponOf(int type)
        {
            for (int i = 0; i < WeaponCount; i++) if ((I32(WeaponOffset(i)) & 0xFFFF) == type) return WeaponAt(i);
            return null;
        }

        public List<AttackMode> AttackModeRows()
        {
            int n = I32(AttackModeCountAt);
            var list = new List<AttackMode>(n);
            for (int i = 0; i < n; i++)
            {
                int o = AttackModes + AttackModeSize * i;
                list.Add(new AttackMode(i, o, I32(o), I32(o + 4), I32(o + 8) != 0, I32(o + 12), I32(o + 16)));
            }
            return list;
        }

        /// <summary>The attack-mode row the game uses for a weapon type and mode: one keyed by the type first, else by its class.</summary>
        public AttackMode? AttackModeOf(int type, int weaponClass, int mode)
        {
            var rows = AttackModeRows();
            foreach (var r in rows) if (r.Key == type && r.Mode == mode) return r;
            foreach (var r in rows) if (r.Key == weaponClass && r.Mode == mode) return r;
            return null;
        }

        public List<ArmourPiece> ArmourPieces()
        {
            var list = new List<ArmourPiece>();
            for (int i = 0; i < ArmourCount; i++)
            {
                int o = Armour + ArmourSize * i;
                if (I32(o) == 0) continue;
                list.Add(new ArmourPiece(i, o, I32(o), I32(o + 4), I32(o + 8), image[o + 12], image[o + 13]));
            }
            return list;
        }

        public List<NaturalAttack> NaturalAttacks()
        {
            var list = new List<NaturalAttack>(NaturalCount);
            for (int i = 0; i < NaturalCount; i++)
            {
                int o = Natural + NaturalSize * i;
                list.Add(new NaturalAttack(i, o, I32(o), I32(o + 4), F32(o + 8), F32(o + 12), I32(o + 16), I32(o + 20), BoneName(i)));
            }
            return list;
        }

        string BoneName(int i)
        {
            uint va = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(NaturalBones + 8 * i));
            long o = va - DataDelta;
            if (o <= 0 || o >= original.Length) return "";
            int end = Array.IndexOf(original, (byte)0, (int)o, 64);
            return end < 0 ? "" : System.Text.Encoding.ASCII.GetString(original, (int)o, end - (int)o);
        }

        /// <summary>The damage multiplier of a creature kind (0-63) for a damage-type bit (0-10).</summary>
        public float Multiplier(int kind, int bit) => multipliers[kind * Bits + bit];

        /// <summary>The 11 multipliers of a kind; all 1 for kinds out of range (the game reads past the table for -1, see audit.md).</summary>
        public float[] MultipliersOf(int kind)
        {
            if (kind < 0 || kind >= Kinds) return Enumerable.Repeat(1f, Bits).ToArray();
            return multipliers.AsSpan(kind * Bits, Bits).ToArray();
        }

        /// <summary>
        /// The factor for an attack's damage flags against a kind (FUN_00449F10): the product of the non-zero multipliers
        /// of the set bits, or 0 when those multipliers add up to 0 or less (immune), or when there are no flags.
        /// </summary>
        public static float Combine(float[] row, int flags)
        {
            if (flags == 0) return 0;
            float sum = 0, product = 1;
            for (int b = 0; b < Bits; b++)
            {
                if ((flags & (1 << b)) == 0) continue;
                sum += row[b];
                if (row[b] != 0) product *= row[b];
            }
            return sum <= 0 ? 0 : product;
        }

        /// <summary>Whether a multiplier can change without rewriting the start-up code (some are pushed as a one-byte 0).</summary>
        public bool CanChangeInPlace(int kind, int bit) => sites[kind * Bits + bit] >= 0;
        public bool MultipliersAreTable => tableAt >= 0;

        static readonly byte[] StubHead = { 0x56, 0x57, 0xBE };   // push esi; push edi; mov esi, imm32

        static bool IsTableStub(byte[] b)
        {
            if (!b.AsSpan(Builder, 3).SequenceEqual(StubHead)) return false;
            return b[Builder + 7] == 0xBF && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(Builder + 8)) == MultiplierVa;
        }

        void ReadMultipliers()
        {
            multipliers = new float[Kinds * Bits];
            sites = Enumerable.Repeat(-1, Kinds * Bits).ToArray();
            if (IsTableStub(image))
            {
                tableAt = (int)(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(Builder + 3)) - TextDelta);
                for (int s = 0; s < multipliers.Length; s++) { sites[s] = tableAt + 4 * s; multipliers[s] = F32(sites[s]); }
                return;
            }
            var pushes = new List<(int Site, uint Value)>();
            uint ecx = 0;
            int p = Builder;
            void Put(uint address, int site, uint value)
            {
                long rel = (long)address - MultiplierVa;
                if (rel < 0 || rel >= 4L * Kinds * Bits || rel % 4 != 0) throw new InvalidDataException($"Damage-multiplier code writes outside the table (0x{address:X}).");
                int s = (int)(rel / 4);
                multipliers[s] = BitConverter.UInt32BitsToSingle(value);
                sites[s] = site;
            }
            while (p < BuilderEnd)
            {
                byte op = image[p];
                if (op == 0xC7 && image[p + 1] == 0x05) { Put(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 2)), p + 6, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 6))); p += 10; }
                else if (op == 0x68) { pushes.Add((p + 1, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 1)))); p += 5; }
                else if (op == 0x6A) { pushes.Add((-1, (uint)(sbyte)image[p + 1])); p += 2; }
                else if (op == 0xB9) { ecx = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p + 1)); p += 5; }
                else if (op == 0xE8)
                {
                    uint target = (uint)(p + TextDelta + 5 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(p + 1)));
                    if (target != HelperVa || pushes.Count < Bits) throw new InvalidDataException($"Unexpected call in the damage-multiplier code at VA 0x{p + TextDelta:X}.");
                    for (int i = 0; i < Bits; i++)
                    {
                        var (site, value) = pushes[pushes.Count - 1 - i];   // the last push is the first argument
                        Put(ecx + (uint)(4 * i), site, value);
                    }
                    pushes.RemoveRange(pushes.Count - Bits, Bits);
                    p += 5;
                }
                else if (op == 0x90) p++;
                else if (op == 0xC3) break;
                else throw new InvalidDataException($"Unexpected instruction 0x{op:X2} in the damage-multiplier code at VA 0x{p + TextDelta:X}.");
            }
        }

        // ---- editing ----

        /// <summary>Raised after any change to the image (offset, length).</summary>
        public event Action<int, int> Changed;

        /// <summary>Writes bytes into the image (the editor's undo goes through here).</summary>
        public void WriteBytes(int offset, ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(image.AsSpan(offset));
            if (offset < BuilderEnd && offset + bytes.Length > Builder || tableAt >= 0 && offset < tableAt + 4 * Kinds * Bits && offset + bytes.Length > tableAt)
            {
                tableAt = -1;
                ReadMultipliers();
            }
            Changed?.Invoke(offset, bytes.Length);
        }

        public byte[] ReadBytes(int offset, int length) => image.AsSpan(offset, length).ToArray();

        static byte[] Encode(StatKind kind, double value)
        {
            switch (kind)
            {
                case StatKind.U8: return new[] { (byte)Math.Clamp((int)Math.Round(value), 0, 255) };
                case StatKind.I32: { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)); return b; }
                default: { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, (float)value); return b; }
            }
        }

        public double Get(ExeStat s) => s.Kind switch { StatKind.U8 => image[s.Offset], StatKind.I32 => I32(s.Offset), _ => F32(s.Offset) };
        double GetOriginal(ExeStat s) => s.Kind switch
        {
            StatKind.U8 => original[s.Offset],
            StatKind.I32 => BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(s.Offset)),
            _ => BinaryPrimitives.ReadSingleLittleEndian(original.AsSpan(s.Offset)),
        };

        /// <summary>The (offset, bytes before, bytes after) of setting a stat; null when nothing changes.</summary>
        public (int Offset, byte[] Before, byte[] After)? Set(ExeStat s, double value)
        {
            var now = Encode(s.Kind, value);
            var before = ReadBytes(s.Offset, s.Size);
            if (before.AsSpan().SequenceEqual(now)) return null;
            WriteBytes(s.Offset, now);
            return (s.Offset, before, now);
        }

        public static string WeaponName(int type, Func<int, string> names) => names?.Invoke(type) ?? "type " + type;

        public ExeStat WeaponMin(int index, string name) => new ExeStat(WeaponOffset(index) + 8, StatKind.I32, $"{name}: {(WeaponAt(index).IsShield ? "min block" : "min damage")}");
        public ExeStat WeaponMax(int index, string name) => new ExeStat(WeaponOffset(index) + 12, StatKind.I32, $"{name}: {(WeaponAt(index).IsShield ? "max block" : "max damage")}");
        public static ExeStat WeaponReach(int index, string name) => new ExeStat(WeaponOffset(index) + 16, StatKind.F32, $"{name}: reach");
        public static ExeStat WeaponRadius(int index, string name) => new ExeStat(WeaponOffset(index) + 20, StatKind.F32, $"{name}: hit radius");
        public static ExeStat AttackFlags(int row, string what) => new ExeStat(AttackModes + AttackModeSize * row + 12, StatKind.I32, $"{what}: damage types");
        public static ExeStat ArmourMin(int index, string name) => new ExeStat(Armour + ArmourSize * index + 12, StatKind.U8, $"{name}: min defence");
        public static ExeStat ArmourMax(int index, string name) => new ExeStat(Armour + ArmourSize * index + 13, StatKind.U8, $"{name}: max defence");
        public static ExeStat NaturalMin(int index) => new ExeStat(Natural + NaturalSize * index, StatKind.I32, $"natural attack {index}: min damage");
        public static ExeStat NaturalMax(int index) => new ExeStat(Natural + NaturalSize * index + 4, StatKind.I32, $"natural attack {index}: max damage");
        public static ExeStat NaturalFlags(int index) => new ExeStat(Natural + NaturalSize * index + 16, StatKind.I32, $"natural attack {index}: damage types");

        /// <summary>
        /// Sets a multiplier. Where the start-up code pushes it as a one-byte value, the code is first rewritten as a table
        /// copy (stub at VA 0x44A040, table right after it), which makes every multiplier a plain float. Returns the bytes
        /// changed (offset, before, after) for undo; null when nothing changes.
        /// </summary>
        public (int Offset, byte[] Before, byte[] After)? SetMultiplier(int kind, int bit, float value)
        {
            if (kind < 0 || kind >= Kinds || bit < 0 || bit >= Bits) throw new ArgumentOutOfRangeException(nameof(kind));
            int s = kind * Bits + bit;
            if (multipliers[s] == value) return null;
            if (sites[s] < 0 || tableAt >= 0)
            {
                var before = ReadBytes(Builder, BuilderEnd - Builder);
                var values = (float[])multipliers.Clone();
                values[s] = value;
                // Back to the original values: the original code comes back too.
                var after = values.AsSpan().SequenceEqual(originalMultipliers) ? original.AsSpan(Builder, BuilderEnd - Builder).ToArray() : TableCode(values);
                if (before.AsSpan().SequenceEqual(after)) return null;
                WriteBytes(Builder, after);
                return (Builder, before, after);
            }
            return Set(new ExeStat(sites[s], StatKind.F32, ""), value);
        }

        /// <summary>The builder rewritten: copy 704 floats from a table in the same code into the multiplier memory.</summary>
        byte[] TableCode(float[] values)
        {
            var b = ReadBytes(Builder, BuilderEnd - Builder);
            uint table = (uint)(Builder + 0x20 + TextDelta);
            var code = new List<byte> { 0x56, 0x57, 0xBE };                       // push esi; push edi; mov esi, table
            code.AddRange(BitConverter.GetBytes(table));
            code.Add(0xBF); code.AddRange(BitConverter.GetBytes(MultiplierVa));    // mov edi, 0x84E598
            code.Add(0xB9); code.AddRange(BitConverter.GetBytes(Kinds * Bits));    // mov ecx, 704
            code.AddRange(new byte[] { 0xFC, 0xF3, 0xA5, 0x5F, 0x5E, 0xC3 });      // cld; rep movsd; pop edi; pop esi; ret
            while (code.Count < 0x20) code.Add(0xCC);
            code.CopyTo(b, 0);
            for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0x20 + 4 * i), values[i]);
            return b;
        }

        // ---- what changed ----

        /// <summary>One change against the original, with a note saying what it is.</summary>
        public readonly record struct Change(int Offset, byte[] Old, byte[] New, string Note);

        /// <summary>
        /// Everything that differs from the original: one change per known stat (named, with old and new value), and the
        /// rewritten multiplier code as one change listing the multipliers it alters.
        /// </summary>
        public List<Change> Changes(Func<int, string> typeNames = null, Func<int, string> kindNames = null)
        {
            var list = new List<Change>();
            void Stat(ExeStat s)
            {
                if (image.AsSpan(s.Offset, s.Size).SequenceEqual(original.AsSpan(s.Offset, s.Size))) return;
                string F(double v) => s.Kind == StatKind.F32 ? v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : ((long)v).ToString();
                list.Add(new Change(s.Offset, original.AsSpan(s.Offset, s.Size).ToArray(), ReadBytes(s.Offset, s.Size), $"{s.Label} {F(GetOriginal(s))} -> {F(Get(s))}"));
            }
            foreach (var w in Weapons())
            {
                string n = typeNames?.Invoke(w.Type) is string wn ? $"{wn} ({w.Type})" : $"type {w.Type}";
                Stat(WeaponMin(w.Index, n)); Stat(WeaponMax(w.Index, n)); Stat(WeaponReach(w.Index, n)); Stat(WeaponRadius(w.Index, n));
            }
            foreach (var r in AttackModeRows())
                Stat(AttackFlags(r.Index, (r.Key < 16 ? ClassNames.ElementAtOrDefault(r.Key) ?? "class " + r.Key : WeaponName(r.Key, typeNames)) + $" attack {r.Mode + 1}"));
            foreach (var a in ArmourPieces())
            {
                string n = WeaponName(a.Type, typeNames);
                Stat(ArmourMin(a.Index, n)); Stat(ArmourMax(a.Index, n));
            }
            for (int i = 0; i < NaturalCount; i++) { Stat(NaturalMin(i)); Stat(NaturalMax(i)); Stat(NaturalFlags(i)); }

            string K(int k) => kindNames?.Invoke(k) is string s && s.Length > 0 ? $"kind {k} ({s})" : $"kind {k}";
            if (tableAt >= 0 && !image.AsSpan(Builder, BuilderEnd - Builder).SequenceEqual(original.AsSpan(Builder, BuilderEnd - Builder)))
            {
                var changed = new List<string>();
                for (int s = 0; s < multipliers.Length; s++)
                    if (originalMultipliers[s] != multipliers[s]) changed.Add($"{K(s / Bits)} {BitNames[s % Bits]} x{originalMultipliers[s]:0.###} -> x{multipliers[s]:0.###}");
                int start = Builder, end = BuilderEnd;
                while (start < end && image[start] == original[start]) start++;
                while (end > start && image[end - 1] == original[end - 1]) end--;
                list.Add(new Change(start, original.AsSpan(start, end - start).ToArray(), ReadBytes(start, end - start),
                    "damage multipliers as a table (start-up code at VA 0x44A040 rewritten)" + (changed.Count > 0 ? ": " + string.Join("; ", changed) : "")));
            }
            else
            {
                for (int s = 0; s < multipliers.Length; s++)
                {
                    int o = sites[s];
                    if (o < 0 || image.AsSpan(o, 4).SequenceEqual(original.AsSpan(o, 4))) continue;
                    list.Add(new Change(o, original.AsSpan(o, 4).ToArray(), ReadBytes(o, 4), $"{K(s / Bits)} {BitNames[s % Bits]} multiplier x{originalMultipliers[s]:0.###} -> x{multipliers[s]:0.###}"));
                }
            }
            // Anything else the image changed (not a known stat): plain runs.
            var covered = new HashSet<int>();
            foreach (var c in list) for (int i = 0; i < c.New.Length; i++) covered.Add(c.Offset + i);
            foreach (var (o, n) in DiffRuns())
                for (int i = o; i < o + n; i++)
                {
                    if (covered.Contains(i)) continue;
                    int j = i;
                    while (j < o + n && !covered.Contains(j)) j++;
                    list.Add(new Change(i, original.AsSpan(i, j - i).ToArray(), ReadBytes(i, j - i), "other change"));
                    i = j;
                }
            list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return list;
        }

        IEnumerable<(int Offset, int Length)> DiffRuns()
        {
            int i = 0, n = image.Length;
            while (i < n)
            {
                int d = original.AsSpan(i).CommonPrefixLength(image.AsSpan(i));
                i += d;
                if (i >= n) yield break;
                int start = i;
                while (i < n && original[i] != image[i]) i++;
                yield return (start, i - start);
            }
        }

        public bool HasChanges => !original.AsSpan().SequenceEqual(image);
    }
}
