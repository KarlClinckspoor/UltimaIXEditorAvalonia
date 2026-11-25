using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Combat;
using U9.Mods;

namespace U9Editor.Core
{
    /// <summary>
    /// The combat stats of a game folder being edited: u9.exe's tables (changes kept as u9.exe.patches.json, never
    /// written into u9.exe) and runtime/NPC.FLX (saved with a .orig copy, like the editor's other files). Undo and redo
    /// cover both.
    /// </summary>
    public sealed class CombatEditing
    {
        public CombatTables Tables { get; }
        public NpcTable Npcs { get; }
        public string Root { get; }
        public string ExePath { get; }
        public string NpcPath { get; }
        readonly FileId exeId;
        public Func<int, string> TypeNames { get; set; }

        abstract record Step;
        sealed record ExeStep(int Offset, byte[] Before, byte[] After) : Step;
        sealed record NpcStep(int Index, byte[] Before, byte[] After) : Step;

        readonly List<Step> undo = new(), redo = new();
        int savedAt;          // undo count at the last save (-1: the saved state is gone)

        /// <summary>Raised after every change, undo and redo.</summary>
        public event Action Changed;

        CombatEditing(string root, string exePath, CombatTables tables, NpcTable npcs, FileId exe)
        {
            Root = root; ExePath = exePath; Tables = tables; Npcs = npcs; NpcPath = npcs.Path; exeId = exe;
        }

        /// <summary>Opens a game folder: u9.exe with the pending patches applied, and NPC.FLX. Throws with a reason when it cannot.</summary>
        public static CombatEditing Open(string root)
        {
            string exe = GamePaths.FindExe(root) ?? throw new FileNotFoundException("No u9.exe in " + root);
            var bytes = File.ReadAllBytes(exe);
            string why = CombatTables.Check(bytes);
            if (why != null) throw new InvalidDataException(why);
            var tables = CombatTables.Read(bytes);
            var pending = ExePatchFile.Load(ExePatchFile.PathIn(root), bytes);
            if (pending != null) foreach (var p in pending) tables.WriteBytes(p.Offset, p.New);
            string npc = GamePaths.Find(root, "runtime/npc.flx") ?? throw new FileNotFoundException("No runtime/NPC.FLX in " + root);
            var ed = new CombatEditing(root, exe, tables, NpcTable.Load(npc), FileId.Of(bytes));
            if (GamePaths.Find(root, "static/typename.flx") is string tn)
            {
                var names = U9.Formats.Objects.TypeNames.Load(tn);
                ed.TypeNames = t => names[t];
            }
            return ed;
        }

        public bool HasChanges => undo.Count != savedAt;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;

        void Push(Step s)
        {
            if (savedAt > undo.Count) savedAt = -1;
            undo.Add(s);
            redo.Clear();
            Changed?.Invoke();
        }

        /// <summary>Sets a stat of u9.exe's tables. False when the value was already that.</summary>
        public bool Set(ExeStat stat, double value)
        {
            var r = Tables.Set(stat, value);
            if (r is not { } c) return false;
            Push(new ExeStep(c.Offset, c.Before, c.After));
            return true;
        }

        public bool SetMultiplier(int kind, int bit, float value)
        {
            var r = Tables.SetMultiplier(kind, bit, value);
            if (r is not { } c) return false;
            Push(new ExeStep(c.Offset, c.Before, c.After));
            return true;
        }

        public bool SetNpc(int index, NpcField field, int value)
        {
            var before = Npcs.RecordBytes(index);
            Npcs.Set(index, field, value);
            var after = Npcs.RecordBytes(index);
            if (before.AsSpan().SequenceEqual(after)) return false;
            Push(new NpcStep(index, before, after));
            return true;
        }

        void Put(Step s, bool forward)
        {
            switch (s)
            {
                case ExeStep e: Tables.WriteBytes(e.Offset, forward ? e.After : e.Before); break;
                case NpcStep n: Npcs.PutRecord(n.Index, forward ? n.After : n.Before); break;
            }
        }

        public void Undo()
        {
            if (undo.Count == 0) return;
            var s = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            Put(s, false);
            redo.Add(s);
            Changed?.Invoke();
        }

        public void Redo()
        {
            if (redo.Count == 0) return;
            var s = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            Put(s, true);
            undo.Add(s);
            Changed?.Invoke();
        }

        /// <summary>The executable changes as patches with notes (what a mod gets).</summary>
        public List<ExePatch> ExePatchList(Func<int, string> kindNames = null) =>
            Tables.Changes(TypeNames, kindNames).Select(c => new ExePatch { Offset = c.Offset, Old = c.Old, New = c.New, Note = c.Note }).ToList();

        public bool NpcsChanged => !File.Exists(NpcPath) || !Npcs.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(NpcPath));

        /// <summary>
        /// Writes u9.exe.patches.json (deleted when nothing is left) and NPC.FLX (keeping NPC.FLX.orig the first time).
        /// Refuses executable changes that touch U9GL's signature. Returns the paths written.
        /// </summary>
        public List<string> Save(Func<int, string> kindNames = null)
        {
            var written = new List<string>();
            var patches = ExePatchList(kindNames);
            var clashes = ExePatches.U9glClashes(Tables.Original, patches);
            if (clashes.Count > 0) throw new InvalidOperationException("The executable changes touch the code U9GL checks: " + string.Join("; ", clashes));
            string patchPath = ExePatchFile.PathIn(Root);
            if (patches.Count > 0 || File.Exists(patchPath)) { ExePatchFile.Save(patchPath, exeId, patches); written.Add(patchPath); }
            if (NpcsChanged)
            {
                string backup = NpcPath + ".orig";
                if (File.Exists(NpcPath) && !File.Exists(backup)) File.Copy(NpcPath, backup);
                Npcs.Save(NpcPath);
                written.Add(NpcPath);
            }
            savedAt = undo.Count;
            Changed?.Invoke();
            return written;
        }
    }
}
