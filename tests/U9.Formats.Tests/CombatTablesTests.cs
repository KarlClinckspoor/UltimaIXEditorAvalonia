using System;
using System.IO;
using System.Linq;
using U9.Formats.Combat;
using Xunit;

namespace U9.Formats.Tests
{
    public class CombatTablesTests
    {
        static CombatTables Tables() => CombatTables.Load(Path.Combine(GameData.Require(), "u9.exe"));

        [Fact]
        public void Reads_the_weapon_armour_and_natural_tables()
        {
            var t = Tables();
            Assert.Equal(65, t.WeaponCount);
            var longsword = t.WeaponOf(5388).Value;
            Assert.Equal((1, 40, 60, 30f), (longsword.Class, longsword.Min, longsword.Max, longsword.Reach));
            Assert.Equal(500, t.WeaponOf(4777).Value.Min);                 // Glass Sword
            Assert.Equal(4000f, t.WeaponOf(4846).Value.Reach);             // Siege Bow range
            Assert.Equal(10f, t.WeaponOf(3780).Value.Radius);              // T2 Broadsword (T1's is 20)
            Assert.Equal(53, t.AttackModeRows().Count);
            Assert.Equal(0x41, t.AttackModeOf(4773, 1, 0).Value.Flags);    // Flaming Sword: slash + fire
            Assert.Equal(0x04, t.AttackModeOf(5388, 1, 1).Value.Flags);    // Longsword falls back to its class
            var armour = t.ArmourPieces();
            Assert.Equal(55, armour.Where(a => a.Level == 3).Sum(a => a.Max));   // full plate
            Assert.Equal(75, armour.Where(a => a.Level == 6).Sum(a => a.Max));   // full blackrock
            var nat = t.NaturalAttacks();
            Assert.Equal((70, 80, 0x40, "HEAD"), (nat[22].Min, nat[22].Max, nat[22].Flags, nat[22].Bone));
            Assert.Equal("RIGHT_HAND", nat[0].Bone);
        }

        [Fact]
        public void Walks_the_multiplier_code_like_the_emulation()
        {
            var t = Tables();
            // Rows from the emulated start-up code (analysis/item-stats/tables.txt).
            Assert.Equal(new[] { 2f, 2, 0.25f, 0.25f, 1, 2, 2, 0.1f, 1, 1, 2 }, t.MultipliersOf(4));        // Crusty
            Assert.Equal(new[] { 0.25f, 2, 0.5f, 0, 0.5f, 0.5f, 0.25f, 0.5f, 0, 1, 0.5f }, t.MultipliersOf(22)); // Skeleton
            Assert.Equal(new[] { 1f, 0.25f, 1, 0.25f, 1, 1, 1, 1, 1, 1, 1 }, t.MultipliersOf(40));
            Assert.Equal(new[] { 0f, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1 }, t.MultipliersOf(61));                  // ghosts
            Assert.Equal(1f, t.Multiplier(63, 0));
            Assert.All(Enumerable.Range(0, 11), b => Assert.Equal(0f, t.Multiplier(16, b)));
            Assert.Equal(1f, CombatTables.Combine(t.MultipliersOf(-1), 1));
        }

        [Fact]
        public void Combines_flags_like_the_game()
        {
            var ghost = new[] { 0f, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1 };
            Assert.Equal(0f, CombatTables.Combine(ghost, 0x1));        // immune to slash
            Assert.Equal(1f, CombatTables.Combine(ghost, 0x41));       // fire gets through; the 0 is skipped in the product
            Assert.Equal(0f, CombatTables.Combine(ghost, 0));
            var crusty = new[] { 2f, 2, 0.25f, 0.25f, 1, 2, 2, 0.1f, 1, 1, 2 };
            Assert.Equal(4f, CombatTables.Combine(crusty, 0x41));      // slash x fire
        }

        [Fact]
        public void Edits_stats_and_lists_them_as_changes()
        {
            var t = Tables();
            int i = t.WeaponOf(5388).Value.Index;
            Assert.NotNull(t.Set(t.WeaponMax(i, "Longsword"), 80));
            Assert.Null(t.Set(t.WeaponMax(i, "Longsword"), 80));
            Assert.Equal(80, t.WeaponOf(5388).Value.Max);
            var a = t.ArmourPieces().First(p => p.Type == 4638);
            t.Set(CombatTables.ArmourMax(a.Index, "Plate chest"), 30);
            var c = t.Changes(n => n == 5388 ? "Longsword" : null);
            Assert.Equal(2, c.Count);
            Assert.Contains(c, x => x.Note == "Longsword (5388): max damage 60 -> 80" && x.Offset == CombatTables.WeaponOffset(i) + 12);
            Assert.Contains(c, x => x.Note.EndsWith("max defence 18 -> 30"));
            t.Set(t.WeaponMax(i, "Longsword"), 60);
            t.Set(CombatTables.ArmourMax(a.Index, ""), 18);
            Assert.Empty(t.Changes());
            Assert.False(t.HasChanges);
        }

        [Fact]
        public void Changes_a_multiplier_in_place_or_rewrites_the_code_as_a_table()
        {
            var t = Tables();
            Assert.True(t.CanChangeInPlace(4, 6));
            t.SetMultiplier(4, 6, 0.5f);                                     // Crusty fire
            Assert.Equal(0.5f, t.Multiplier(4, 6));
            Assert.False(t.MultipliersAreTable);
            var one = Assert.Single(t.Changes());
            Assert.Equal(4, one.New.Length);
            Assert.Contains("fire multiplier x2 -> x0.5", one.Note);

            // A multiplier the code pushes as a one-byte 0: the code becomes a table copy, every value kept.
            int k = Enumerable.Range(0, 64).First(kk => Enumerable.Range(0, 11).Any(b => !t.CanChangeInPlace(kk, b)));
            int bit = Enumerable.Range(0, 11).First(b => !t.CanChangeInPlace(k, b));
            var before = Enumerable.Range(0, 64 * 11).Select(s => t.Multiplier(s / 11, s % 11)).ToArray();
            t.SetMultiplier(k, bit, 3f);
            Assert.True(t.MultipliersAreTable);
            for (int s = 0; s < before.Length; s++)
                Assert.Equal(s == k * 11 + bit ? 3f : before[s], t.Multiplier(s / 11, s % 11));
            var again = CombatTables.Read(t.Image);                         // the rewritten image reads back the same
            Assert.True(again.MultipliersAreTable);
            Assert.Equal(3f, again.Multiplier(k, bit));
            Assert.Equal(0.5f, again.Multiplier(4, 6));
            var change = Assert.Single(t.Changes());
            Assert.Contains("table", change.Note);
            Assert.Contains("x0 -> x3", change.Note);
        }

        [Fact]
        public void Refuses_another_build()
        {
            var bytes = File.ReadAllBytes(Path.Combine(GameData.Require(), "u9.exe"));
            bytes[CombatTables.WeaponT1] ^= 1;
            Assert.Throws<InvalidDataException>(() => CombatTables.Read(bytes));
            Assert.NotNull(CombatTables.Check(new byte[100]));
        }

        [Fact]
        public void Reads_and_edits_npc_records()
        {
            var path = Path.Combine(GameData.Require(), "runtime", "NPC.FLX");
            var npcs = NpcTable.Load(path);
            Assert.Equal(352, npcs.Count);
            Assert.Equal(("Avatar", 30, 0), (npcs[0].Name, npcs[0].MaxHp, npcs[0].Kind));
            var hound = npcs.Records().First(r => r.Name == "Hellhound");
            Assert.Equal((99, 40, 27), (hound.MaxHp, hound.Defence, hound.Kind));
            Assert.True(hound.Fighter);
            Assert.True(npcs[1].Invulnerable);                                // Lord British
            npcs.Set(hound.Index, NpcField.Hp, 150);
            npcs.Set(hound.Index, NpcField.Defence, 300);                     // clamped to a byte
            var back = NpcTable.Read(npcs.ToBytes());
            Assert.Equal((150, 150, 150, 255), (back[hound.Index].Hp, back[hound.Index].MaxHp, back[hound.Index].BaseHp, back[hound.Index].Defence));
            npcs.Set(hound.Index, NpcField.Hp, 99);
            npcs.Set(hound.Index, NpcField.Defence, 40);
            Assert.Equal(File.ReadAllBytes(path), npcs.ToBytes());
        }
    }
}
