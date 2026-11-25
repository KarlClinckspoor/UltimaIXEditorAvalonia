using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using U9.Formats.Flx;

namespace U9.Formats.Combat
{
    /// <summary>An editable field of an NPC.FLX record.</summary>
    public enum NpcField { Hp, Defence, Kind, Mana }

    /// <summary>One NPC.FLX record: a character or creature template (its type number is the record number).</summary>
    public readonly record struct NpcRecord(int Index, string Name, int Hp, int MaxHp, int BaseHp, int Mana, int Defence, int Kind, uint Flags48, uint FlagsC4, int Strength = 0)
    {
        /// <summary>Takes no damage: +0x48 bit 0 or +0xC4 bit 2 (0x43FA10, audit.md).</summary>
        public bool Invulnerable => (Flags48 & 1) != 0 || (FlagsC4 & 4) != 0;
        /// <summary>The hostile-creature templates seem to carry 0x08000000 in +0x48 (bandits, gargoyles, undead, beasts).</summary>
        public bool Fighter => (Flags48 & 0x08000000) != 0;
        /// <summary>Undead or demon: the Staff of the Dead and Gringolet add 10 (+0xC4 bit 0x200).</summary>
        public bool UndeadOrDemon => (FlagsC4 & 0x200) != 0;
        /// <summary>Its attacks do a tenth more (+0xC4 bit 0x400).</summary>
        public bool HitsHarder => (FlagsC4 & 0x400) != 0;
        public override string ToString() => $"{Index} {Name}";
    }

    /// <summary>
    /// runtime/NPC.FLX: record 0 holds 352 NPC records of 316 bytes (analysis/item-stats/report.md, audited). The game
    /// copies them into a new game; saved games keep their own copy, so changes reach new games only.
    /// </summary>
    public sealed class NpcTable
    {
        public const int RecordSize = 316, NameOffset = 4, NameLength = 0x20;
        readonly FlxArchive flx;
        readonly byte[] data;
        public string Path { get; }

        NpcTable(FlxArchive archive, string path)
        {
            flx = archive;
            Path = path;
            data = archive.Read(0) ?? throw new InvalidDataException("NPC.FLX has no record 0.");
            if (data.Length % RecordSize != 0) throw new InvalidDataException($"NPC.FLX record 0 is {data.Length} bytes, not a whole number of {RecordSize}-byte records.");
        }

        public static NpcTable Load(string path)
        {
            using var s = File.OpenRead(path);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return new NpcTable(FlxArchive.Open(ms), path);
        }

        public static NpcTable Read(byte[] flxBytes) => new NpcTable(FlxArchive.Open(new MemoryStream(flxBytes)), null);

        public int Count => data.Length / RecordSize;

        int U16(int i, int o) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * RecordSize + o));
        int I32(int i, int o) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * RecordSize + o));

        public NpcRecord this[int i]
        {
            get
            {
                var raw = data.AsSpan(i * RecordSize + NameOffset, NameLength);
                int z = raw.IndexOf((byte)0);
                string name = Encoding.Latin1.GetString(z < 0 ? raw : raw[..z]);
                return new NpcRecord(i, name, U16(i, 0x34), U16(i, 0x36), U16(i, 0x38), U16(i, 0x3C), data[i * RecordSize + 0x26],
                    I32(i, 0x44), (uint)I32(i, 0x48), (uint)I32(i, 0xC4), I32(i, 0x28));
            }
        }

        public IEnumerable<NpcRecord> Records() => Enumerable.Range(0, Count).Select(i => this[i]);

        /// <summary>Where a field is: (offsets, size). HP writes current, maximum and base maximum; mana its maximum and base.</summary>
        static (int[] Offsets, int Size) Where(NpcField f) => f switch
        {
            NpcField.Hp => (new[] { 0x34, 0x36, 0x38 }, 2),
            NpcField.Mana => (new[] { 0x3A, 0x3C, 0x3E }, 2),
            NpcField.Defence => (new[] { 0x26 }, 1),
            NpcField.Kind => (new[] { 0x44 }, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        public static (int Min, int Max) RangeOf(NpcField f) => f switch
        {
            NpcField.Hp or NpcField.Mana => (0, 0xFFFF), NpcField.Defence => (0, 255), NpcField.Kind => (-1, CombatTables.Kinds - 1), _ => (0, 0),
        };

        public int Get(int i, NpcField f)
        {
            var (o, size) = Where(f);
            int at = o[f == NpcField.Hp || f == NpcField.Mana ? 1 : 0];
            return size switch { 1 => data[i * RecordSize + at], 2 => U16(i, at), _ => I32(i, at) };
        }

        /// <summary>The bytes of a record (for undo).</summary>
        public byte[] RecordBytes(int i) => data.AsSpan(i * RecordSize, RecordSize).ToArray();
        public void PutRecord(int i, byte[] bytes) { bytes.CopyTo(data.AsSpan(i * RecordSize)); Changed = true; }

        public bool Changed { get; private set; }

        public void Set(int i, NpcField f, int value)
        {
            var (min, max) = RangeOf(f);
            value = Math.Clamp(value, min, max);
            var (offsets, size) = Where(f);
            foreach (int o in offsets)
            {
                var span = data.AsSpan(i * RecordSize + o);
                if (size == 1) span[0] = (byte)value;
                else if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)value);
                else BinaryPrimitives.WriteInt32LittleEndian(span, value);
            }
            Changed = true;
        }

        public byte[] ToBytes()
        {
            flx.Write(0, (byte[])data.Clone());
            var ms = new MemoryStream();
            flx.Save(ms, FlxLayout.Preserve);
            return ms.ToArray();
        }

        /// <summary>Writes the file (through a temporary file).</summary>
        public void Save(string path = null)
        {
            path ??= Path ?? throw new InvalidOperationException("Loaded from bytes: give a path");
            string temp = path + ".new";
            File.WriteAllBytes(temp, ToBytes());
            File.Move(temp, path, true);
            Changed = false;
        }
    }
}
