using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace U9.Formats.Objects
{
    /// <summary>
    /// One property of a nonfixed object: its id, its two u16 parameters, and where they are stored (which 16-byte
    /// segment of the object's chain, which of its three places).
    /// </summary>
    public sealed class ObjectProperty
    {
        public int Id { get; init; }
        public int A { get; init; }
        public int B { get; init; }
        /// <summary>Data offset (from the data start) of the segment that holds it, and its place there (0-2).</summary>
        public int SegmentOffset { get; init; }
        public int Place { get; init; }
        public string Name => PropertyNames.NameOf(Id);
        /// <summary>A and B read as one 32-bit value, A low: how Contents and Next Property Pointer store a data offset.</summary>
        public int Offset32 => A | (B << 16);
        public override string ToString() => $"{Name}: {A}, {B}";
    }

    /// <summary>
    /// The names u9.exe gives property ids: a table of 69 name pointers followed by a parallel table of the ids
    /// (file offsets 0x38D310 and 0x38D440 in the GOG u9.exe). Checked against the shipped objects: Book Number on books,
    /// plaques and grave markers; Quantity on coins and arrows; Light Color / Range / Flicker on light sources; Creature
    /// Type, Spawn Percent and Max Of Type on monster generators; Contents on bags and NPCs carrying things.
    /// </summary>
    public static class PropertyNames
    {
        public const int Contents = 0x05, Quantity = 0x11, BookNumber = 0x4D, NextPropertyPointer = 0xFF;

        static readonly Dictionary<int, string> names = new Dictionary<int, string>
        {
            [0x04] = "Handle", [0x05] = "Contents", [0x06] = "Sound Effect", [0x07] = "Candle Time", [0x08] = "Portal data 1",
            [0x09] = "Portal data 2", [0x0A] = "Avatar's Inventory", [0x10] = "Hit Points", [0x11] = "Quantity", [0x13] = "Owner",
            [0x14] = "Enchantment", [0x15] = "Highway Links", [0x20] = "Translucency", [0x21] = "Light Color", [0x22] = "Light Range",
            [0x23] = "Light Flicker", [0x3B] = "SF Trigger", [0x3C] = "Variable", [0x3D] = "Misc Usecode 1", [0x3E] = "OO Trigger",
            [0x3F] = "Timer", [0x40] = "Trigger Area", [0x41] = "Misc Usecode 2", [0x42] = "Scale", [0x43] = "Combat AI",
            [0x44] = "Default Activity", [0x45] = "Notice Enemy Data", [0x46] = "Home Location XZ", [0x47] = "Home Location YZ",
            [0x48] = "Path Egg Follow Link", [0x49] = "Scale X", [0x4A] = "Scale Y", [0x4B] = "Scale Z", [0x4C] = "Chunk Crossing",
            [0x4D] = "Book Number", [0x4E] = "Tint", [0x4F] = "Supported Item", [0x50] = "Marker Distance", [0x51] = "Marker (x,y)",
            [0x52] = "SunRemover Range", [0x53] = "Hint Level", [0x54] = "Regeneration", [0x55] = "Regeneration time",
            [0x56] = "Npc Alive Time", [0x57] = "Use Distance", [0x58] = "Non Moveable", [0x59] = "Npc Data is Hawkwind",
            [0x5A] = "Ribbon Text Override", [0x60] = "Water Area", [0x61] = "Fog Color", [0x62] = "Fog Range/Dist",
            [0x70] = "Exclude from Backpack", [0x72] = "Spell List1", [0x73] = "Spell List2", [0x74] = "Exclude from Contents",
            [0x75] = "Stun Time (in sec.)", [0x76] = "Min/Max Damage", [0x77] = "Min/Max Defense", [0x80] = "Creature Type",
            [0x81] = "Spawn Percent", [0x82] = "Max Of Type", [0x83] = "Creature Link Num", [0x90] = "Ambiance Min Range",
            [0x91] = "Ambiance Max Range", [0x92] = "Ambiance Sample ID", [0x93] = "Ambiance Sound ID", [0x94] = "Ambiance Max VolPct",
            [0xA0] = "Gump Screen X,Y", [0xFF] = "Next Property Pointer",
        };

        public static string NameOf(int id) => names.TryGetValue(id, out var n) ? n : $"property 0x{id:X2}";
        public static IReadOnlyDictionary<int, string> All => names;
    }

    public sealed partial class ObjectMap
    {
        /// <summary>
        /// Every property of a nonfixed object, following Next Property Pointer (0xFF, whose A and B are the data offset
        /// of the next 16-byte segment) from segment to segment. The pointers themselves are left out.
        /// </summary>
        public List<ObjectProperty> PropertiesOf(MapObject o)
        {
            var list = new List<ObjectProperty>();
            if (Kind != ObjectFileKind.Nonfixed) return list;
            int seg = o.PropertyOffset;
            var seen = new HashSet<int>();
            while (seg != 0 && DataStart + (long)seg + 16 <= Data.Length && seen.Add(seg))
            {
                var s = Data.AsSpan(DataStart + seg, 16);
                int count = Math.Min(3, (int)s[0]), next = 0;
                for (int k = 0; k < count; k++)
                {
                    int id = s[1 + k], a = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4 + 4 * k)), b = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(6 + 4 * k));
                    if (id == PropertyNames.NextPropertyPointer) { next = a | (b << 16); continue; }
                    list.Add(new ObjectProperty { Id = id, A = a, B = b, SegmentOffset = seg, Place = k });
                }
                seg = next;
            }
            return list;
        }

        /// <summary>
        /// The objects inside a container (or carried by an NPC): Contents' A and B are the data offset of the first
        /// record, and each record's link (+0x00) leads to the next. They are in no subchunk list; the views returned
        /// read and write the file like listed objects, but are not in <see cref="Objects"/>.
        /// </summary>
        public List<MapObject> ContentsOf(MapObject o)
        {
            var list = new List<MapObject>();
            foreach (var p in PropertiesOf(o))
            {
                if (p.Id != PropertyNames.Contents) continue;
                int at = p.Offset32;
                var seen = new HashSet<int>();
                while (at != 0 && DataStart + (long)at + RecordSize <= Data.Length && seen.Add(at) && list.Count < 1000)
                {
                    int rec = DataStart + at;
                    var item = byOffset.TryGetValue(rec, out var listed) ? listed
                        : new MapObject { Owner = this, FileOffset = rec, ChunkX = o.ChunkX, ChunkY = o.ChunkY, Subchunk = -1 };
                    list.Add(item);
                    at = (int)(U32(rec) & ~1u);
                }
            }
            return list;
        }

        /// <summary>
        /// Gives a nonfixed object (or one inside it) a new property: into a free place of its last segment, or a new
        /// segment in its chunk when it has none or the last is full (then the last place's property moves into the new
        /// segment and a Next Property Pointer takes its place, as the shipped chains are built). Undoable inside an edit.
        /// </summary>
        public void AddProperty(MapObject o, int id, int a, int b)
        {
            if (Kind != ObjectFileKind.Nonfixed) throw new InvalidOperationException("Only nonfixed objects have properties");
            if (id == PropertyNames.NextPropertyPointer || id == PropertyNames.Contents || id <= 0 || id > 0xFF)
                throw new ArgumentException($"{PropertyNames.NameOf(id)} cannot be added by hand", nameof(id));
            if ((uint)a > 0xFFFF || (uint)b > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(a), "Property values are 0 to 65535");
            if (PropertiesOf(o).Any(p => p.Id == id)) throw new InvalidOperationException($"It already has {PropertyNames.NameOf(id)}");
            if (byOffset.TryGetValue(o.FileOffset, out var listed) && listed == o) Touch(o);
            int chunk = o.ChunkX / ChunkUnits + o.ChunkY / ChunkUnits * Width;
            int first = o.PropertyOffset;
            if (first == 0 || DataStart + (long)first + 16 > Data.Length)
            {
                var seg = NewSegment(chunk, new[] { (id, a, b) });
                uint flag = U32(o.FileOffset + 0x1C) & 1u;
                PutU32(o.FileOffset + 0x1C, (uint)(seg - DataStart) | flag);
                return;
            }
            // the last segment of the chain
            int last = first;
            var seen = new HashSet<int>();
            while (seen.Add(last))
            {
                int next = 0, n = Math.Min(3, (int)Data[DataStart + last]);
                for (int k = 0; k < n; k++)
                    if (Data[DataStart + last + 1 + k] == PropertyNames.NextPropertyPointer)
                        next = U16(DataStart + last + 4 + 4 * k) | (U16(DataStart + last + 6 + 4 * k) << 16);
                if (next == 0 || DataStart + (long)next + 16 > Data.Length) break;
                last = next;
            }
            int at = DataStart + last, count = Math.Min(3, (int)Data[at]);
            if (count < 3)
            {
                Put(at, new[] { (byte)(count + 1) });
                Put(at + 1 + count, new[] { (byte)id });
                PutU16(at + 4 + 4 * count, a);
                PutU16(at + 6 + 4 * count, b);
                return;
            }
            var moved = (Data[at + 3], U16(at + 12), U16(at + 14));
            int fresh = NewSegment(chunk, new[] { ((int)moved.Item1, moved.Item2, moved.Item3), (id, a, b) });
            int rel = fresh - DataStart;
            Put(at + 3, new[] { (byte)PropertyNames.NextPropertyPointer });
            PutU16(at + 12, rel & 0xFFFF);
            PutU16(at + 14, (int)((uint)rel >> 16));
        }

        int NewSegment(int chunk, (int Id, int A, int B)[] props)
        {
            var s = new byte[16];
            s[0] = (byte)props.Length;
            for (int k = 0; k < props.Length; k++)
            {
                s[1 + k] = (byte)props[k].Id;
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(4 + 4 * k), (ushort)props[k].A);
                BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(6 + 4 * k), (ushort)props[k].B);
            }
            int at = Allocate(chunk, 16);
            Put(at, s);
            CountProperties(at, +1);
            return at;
        }

        /// <summary>
        /// Takes a property off an object: the later places of its segment move down one. A first segment left empty is
        /// let go (the object then has no properties); an emptied segment further down the chain stays, holding nothing.
        /// Undoable inside an edit.
        /// </summary>
        public void RemoveProperty(MapObject o, ObjectProperty p)
        {
            if (Kind != ObjectFileKind.Nonfixed) throw new InvalidOperationException("Only nonfixed objects have properties");
            if (p.SegmentOffset <= 0 || DataStart + (long)p.SegmentOffset + 16 > Data.Length || (uint)p.Place > 2)
                throw new ArgumentException("The property is not in this file", nameof(p));
            int at = DataStart + p.SegmentOffset;
            if (Data[at + 1 + p.Place] != p.Id) throw new InvalidOperationException("The property changed since it was read");
            if (p.Id == PropertyNames.NextPropertyPointer || p.Id == PropertyNames.Contents)
                throw new InvalidOperationException($"{p.Name} holds a file offset and is not removed by hand");
            if (byOffset.TryGetValue(o.FileOffset, out var listed) && listed == o) Touch(o);
            int count = Math.Min(3, (int)Data[at]);
            var seg = Data.AsSpan(at, 16).ToArray();
            for (int k = p.Place; k < count - 1; k++)
            {
                seg[1 + k] = seg[2 + k];
                Array.Copy(seg, 8 + 4 * k, seg, 4 + 4 * k, 4);
            }
            seg[count] = 0;
            Array.Clear(seg, 4 + 4 * (count - 1), 4);
            seg[0] = (byte)(count - 1);
            Put(at, seg);
            if (count - 1 == 0 && p.SegmentOffset == o.PropertyOffset)
            {
                PutU32(o.FileOffset + 0x1C, U32(o.FileOffset + 0x1C) & 1u);
                Put(at, new byte[16]);
                CountProperties(at, -1);
            }
        }

        /// <summary>Sets a property's two parameters where they are stored (undoable inside an edit).</summary>
        public void SetProperty(MapObject o, ObjectProperty p, int a, int b)
        {
            if (Kind != ObjectFileKind.Nonfixed) throw new InvalidOperationException("Only nonfixed objects have properties");
            if (p.SegmentOffset <= 0 || DataStart + (long)p.SegmentOffset + 16 > Data.Length || (uint)p.Place > 2)
                throw new ArgumentException("The property is not in this file", nameof(p));
            if (Data[DataStart + p.SegmentOffset + 1 + p.Place] != p.Id) throw new InvalidOperationException("The property changed since it was read");
            if (p.Id == PropertyNames.NextPropertyPointer || p.Id == PropertyNames.Contents)
                throw new InvalidOperationException($"{p.Name} holds a file offset and is not edited by hand");
            if (byOffset.TryGetValue(o.FileOffset, out var listed) && listed == o) Touch(o);
            PutU16(DataStart + p.SegmentOffset + 4 + 4 * p.Place, a & 0xFFFF);
            PutU16(DataStart + p.SegmentOffset + 6 + 4 * p.Place, b & 0xFFFF);
        }
    }
}
