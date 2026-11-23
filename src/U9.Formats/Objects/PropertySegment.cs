using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace U9.Formats.Objects
{
    /// <summary>
    /// A nonfixed object's 16-byte property segment: byte 0 the number of properties, bytes 1-3 up to three
    /// property ids, then six u16 parameters, an (A, B) pair per property. Ids from u9.exe's name table include
    /// 0x3B SF Trigger and 0x3E OO Trigger (A and B are TRIGGERS.FLX slots), 0x3D / 0x41 Misc Usecode 1 / 2
    /// (A is a slot), 0x40 Trigger Area, 0x3F Timer, 0x42 Scale, 0x15 Highway Links, 0x08 / 0x09 Portal data,
    /// 0x83 Creature Link Num, 0xFF Next Property Pointer.
    /// </summary>
    public sealed class PropertySegment
    {
        public const int SfTrigger = 0x3B, MiscUsecode1 = 0x3D, OoTrigger = 0x3E, TriggerArea = 0x40, MiscUsecode2 = 0x41;
        public int Count { get; }
        public int[] Ids { get; } = new int[3];
        public int[] Params { get; } = new int[6];

        public PropertySegment(ReadOnlySpan<byte> seg)
        {
            Count = seg[0];
            for (int i = 0; i < 3; i++) Ids[i] = seg[1 + i];
            for (int i = 0; i < 6; i++) Params[i] = BinaryPrimitives.ReadUInt16LittleEndian(seg.Slice(4 + 2 * i));
        }

        /// <summary>The TRIGGERS.FLX slots this segment names (trigger and usecode properties).</summary>
        public IEnumerable<int> TriggerSlots()
        {
            for (int k = 0; k < 3; k++)
            {
                int id = Ids[k];
                if (id == OoTrigger || id == SfTrigger) { yield return Params[2 * k]; yield return Params[2 * k + 1]; }
                else if (id == MiscUsecode1 || id == MiscUsecode2) yield return Params[2 * k];
            }
        }
    }

}
