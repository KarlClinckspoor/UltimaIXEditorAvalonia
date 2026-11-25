using System;
using System.IO;
using System.Linq;
using U9.Formats.Combat;
using U9.Mods;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class CombatTests
    {
        /// <summary>u9.exe and runtime/NPC.FLX copied to a temporary folder (deleted afterwards).</summary>
        sealed class TempGame : IDisposable
        {
            public readonly string Root;
            public TempGame()
            {
                var g = GameFolder.FindNear(AppContext.BaseDirectory);
                string env = Environment.GetEnvironmentVariable("U9_GAMEDATA");
                string src = !string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "static")) ? env : g?.Root;
                Assert.SkipWhen(src == null, "Ultima IX game data not found");
                Root = Path.Combine(Path.GetTempPath(), "u9combat-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(Root, "runtime"));
                File.Copy(GamePaths.FindExe(src), Path.Combine(Root, "u9.exe"));
                File.Copy(GamePaths.Find(src, "runtime/npc.flx"), Path.Combine(Root, "runtime", "NPC.FLX"));
                Directory.CreateDirectory(Path.Combine(Root, "static"));
                File.Copy(GamePaths.Find(src, "static/typename.flx"), Path.Combine(Root, "static", "TYPENAME.FLX"));
            }
            public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
        }

        [Fact]
        public void Odds_add_and_map()
        {
            var d = Odds.Uniform(1, 6).Plus(Odds.Uniform(1, 6));
            Assert.Equal(2, d.Min);
            Assert.Equal(12, d.Max);
            Assert.Equal(7, d.Mean, 9);
            Assert.Equal(6 / 36.0, d.P[5], 9);
            var hit = Odds.Uniform(1, 5).Minus(Odds.Uniform(0, 10));    // short sword against a pirate's defence
            Assert.Equal(15 / 55.0, hit.Lands, 9);                     // pairs (a, b) with a > b out of 5 x 11
        }

        [Fact]
        public void Expected_hits_are_exact()
        {
            Assert.Equal(3, CombatMath.ExpectedHits(Odds.Exactly(10), 30), 9);
            Assert.Equal(4, CombatMath.ExpectedHits(Odds.Exactly(10), 31), 9);
            // Half the hits do nothing: twice as many.
            var half = new Odds(0, new[] { 0.5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.5 });
            Assert.Equal(6, CombatMath.ExpectedHits(half, 30), 9);
            // 1 or 2 per hit against 2 HP: one hit half the time, else two: 1.5.
            Assert.Equal(1.5, CombatMath.ExpectedHits(Odds.Uniform(1, 2), 2), 9);
            Assert.True(double.IsPositiveInfinity(CombatMath.ExpectedHits(Odds.Uniform(-5, 0), 10)));
        }

        [Fact]
        public void The_avatar_hits_like_the_game()
        {
            using var g = new TempGame();
            var ed = CombatEditing.Open(g.Root);
            var t = ed.Tables;
            var hound = ed.Npcs.Records().First(n => n.Name == "Hellhound");     // 99 HP, defence 40, kind 27 (blunt x0.5)
            var a = new AvatarSetup { Mode = 3 };                                // a longsword's attack 4 is blunt
            var longsword = t.WeaponOf(5388).Value;
            var h = CombatMath.AvatarHits(t, a, longsword, "Longsword", hound);
            Assert.Equal(20 - 40, h.Damage.Min);                                 // 40 x 0.5 - 40
            Assert.Equal(30, h.Damage.Max);                                      // 60 x 0.5 - 0
            Assert.Contains("x0.5", h.How);
            a.Mode = -1;                                                         // best: slash or thrust (x1)
            var best = CombatMath.AvatarHits(t, a, longsword, "Longsword", hound);
            Assert.Equal(60, best.Damage.Max);
            Assert.True(best.Expected < h.Expected);
            Assert.Equal(2, best.Best);
            a.Strength = 3;                                                      // +8
            Assert.Equal(68, CombatMath.AvatarHits(t, a, longsword, "Longsword", hound).Damage.Max);

            var ghost = ed.Npcs.Records().First(n => n.Name == "GenericGhost1"); // immune to slash/blunt/thrust
            a.Strength = 0;
            Assert.True(CombatMath.AvatarHits(t, a, longsword, "Longsword", ghost).Never);
            var flaming = t.WeaponOf(4773).Value;                                // slash + fire: the 0 is skipped
            Assert.False(CombatMath.AvatarHits(t, a, flaming, "Flaming Sword", ghost).Never);
            Assert.True(CombatMath.AvatarHits(t, a, longsword, "Longsword", ed.Npcs[1]).Never);   // Lord British: invulnerable
        }

        [Fact]
        public void Armour_and_shields_come_off_creature_hits()
        {
            using var g = new TempGame();
            var ed = CombatEditing.Open(g.Root);
            var t = ed.Tables;
            var hound = ed.Npcs.Records().First(n => n.Name == "Hellhound");
            var breath = new Attack("breath", 70, 80, 0x40, 12);
            var a = new AvatarSetup { Hp = 30 };
            var bare = CombatMath.CreatureHits(t, a, hound, breath);
            Assert.Equal(1, bare.Expected, 9);
            var plate = t.ArmourPieces().Where(p => p.Level == 3).ToList();
            for (int s = 0; s < 6; s++) a.Armour[s] = plate.First(p => p.Slot == s).Index;
            Assert.Equal((0, 55), (CombatMath.AvatarDefence(t, a).Min, CombatMath.AvatarDefence(t, a).Max));
            a.Shield = t.WeaponOf(754).Value.Index;                              // kite shield 70-100: 7-10
            Assert.Equal((7, 65), (CombatMath.AvatarDefence(t, a).Min, CombatMath.AvatarDefence(t, a).Max));
            a.Hp = 500;
            var armoured = CombatMath.CreatureHits(t, a, hound, breath);
            Assert.Equal(70 - 65, armoured.Damage.Min);                          // the weakest breath against the best roll
            Assert.True(armoured.Expected > CombatMath.CreatureHits(t, new AvatarSetup { Hp = 500 }, hound, breath).Expected);
            Assert.Equal(5, CombatMath.AvatarBonus(t, new AvatarSetup { Armour = { [1] = t.ArmourPieces().First(p => p.Type == CombatMath.FuryGauntlets).Index } }));
        }

        [Fact]
        public void Edits_undo_save_and_reopen()
        {
            using var g = new TempGame();
            var ed = CombatEditing.Open(g.Root);
            var t = ed.Tables;
            int i = t.WeaponOf(5388).Value.Index;
            int hound = ed.Npcs.Records().First(n => n.Name == "Hellhound").Index;
            Assert.True(ed.Set(t.WeaponMax(i, "Longsword"), 80));
            Assert.True(ed.SetNpc(hound, NpcField.Hp, 150));
            Assert.True(ed.SetMultiplier(31, 7, 0.75f));                         // pushed as a one-byte 0: becomes a table
            Assert.True(ed.HasChanges);
            ed.Undo();
            Assert.False(t.MultipliersAreTable);
            Assert.Equal(0f, t.Multiplier(31, 7));
            ed.Redo();
            Assert.Equal(0.75f, t.Multiplier(31, 7));
            var written = ed.Save();
            Assert.Equal(2, written.Count);
            Assert.False(ed.HasChanges);
            Assert.True(File.Exists(Path.Combine(g.Root, "runtime", "NPC.FLX.orig")));
            Assert.True(File.Exists(ExePatchFile.PathIn(g.Root)));
            Assert.Equal(File.ReadAllBytes(Path.Combine(g.Root, "u9.exe")), ed.Tables.Original);   // u9.exe untouched

            var again = CombatEditing.Open(g.Root);
            Assert.Equal(80, again.Tables.WeaponOf(5388).Value.Max);
            Assert.Equal(0.75f, again.Tables.Multiplier(31, 7));
            Assert.Equal(150, again.Npcs[hound].MaxHp);
            Assert.False(again.HasChanges);

            // A mod from the folder carries the patches with their notes and the NPC.FLX delta.
            var mod = ModMaker.FromEditorCopies(g.Root, new ModPackage { Id = "test.combat", Name = "Combat", Version = "1.0.0" });
            Assert.Single(mod.Files);
            Assert.Contains(mod.ExePatches, p => p.Note.Contains("Longsword") && p.Note.Contains("60 -> 80"));
            Assert.Contains(mod.ExePatches, p => p.Note.Contains("table"));
            var exe = File.ReadAllBytes(Path.Combine(g.Root, "u9.exe"));
            ExePatches.Apply(exe, mod.ExePatches);
            Assert.Equal(again.Tables.Image, exe);

            // Everything back: the patch file goes.
            again.Set(again.Tables.WeaponMax(i, "Longsword"), 60);
            again.SetMultiplier(31, 7, 0f);
            again.SetNpc(hound, NpcField.Hp, 99);
            again.Save();
            Assert.False(File.Exists(ExePatchFile.PathIn(g.Root)));
            Assert.Equal(File.ReadAllBytes(Path.Combine(g.Root, "runtime", "NPC.FLX.orig")), File.ReadAllBytes(Path.Combine(g.Root, "runtime", "NPC.FLX")));
        }

        [Fact]
        public void A_patch_file_for_another_exe_is_refused()
        {
            using var g = new TempGame();
            File.WriteAllText(ExePatchFile.PathIn(g.Root), "{\"format\":1,\"exe\":{\"sha256\":\"00\",\"size\":1},\"patches\":[]}");
            Assert.Throws<InvalidDataException>(() => CombatEditing.Open(g.Root));
        }
    }
}
