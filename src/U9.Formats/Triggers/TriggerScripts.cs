using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using U9.Formats.Flx;

namespace U9.Formats.Triggers
{
    /// <summary>
    /// One trigger op: (u16 op, u16 a, u16 b). The op's low byte is the command; its high byte minus 16 is a signed
    /// offset added to the link number it works on (0x10 = +0, 0x11 = +1, 0x0E = -2).
    /// </summary>
    public readonly struct TriggerOp
    {
        public const int SetLink = 0x11, ChangeMap = 0x1F, End = 0xFF;
        public readonly ushort Op, A, B;
        public TriggerOp(ushort op, ushort a, ushort b) { Op = op; A = a; B = b; }
        public int Command => Op & 0xFF;
        public int LinkOffset => (Op >> 8) - 16;
        public override string ToString() => $"{Op:x4}:{A:x},{B:x}";
    }

    /// <summary>
    /// static/TRIGGERS.FLX: each record is a list of 6-byte <see cref="TriggerOp"/>s ending with op 0x10FF.
    /// Known commands (worked out from the shipped maps):
    /// <list type="bullet">
    /// <item>0x1F change map: B's low byte is the destination map (0 = this map), B's high byte flags (0x01, 0x40,
    /// 0x80; meaning unknown), A the type of the arrival marker (0 = any).</item>
    /// <item>0x11 set link, before a 0x1F: B (plus the op's offset) is the arrival marker's link. A is unknown.</item>
    /// </list>
    /// </summary>
    public sealed class TriggerScripts
    {
        readonly Dictionary<int, TriggerOp[]> scripts = new Dictionary<int, TriggerOp[]>();
        public int Count { get; private set; }

        public static TriggerScripts Load(string path)
        {
            var t = new TriggerScripts();
            using var a = FlxArchive.Open(path);
            t.Count = a.Count;
            foreach (int i in a.UsedIndices())
            {
                var r = a.Read(i);
                var ops = new TriggerOp[r.Length / 6];
                for (int k = 0; k < ops.Length; k++)
                    ops[k] = new TriggerOp(BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(k * 6)),
                                           BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(k * 6 + 2)),
                                           BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(k * 6 + 4)));
                t.scripts[i] = ops;
            }
            return t;
        }

        /// <summary>The ops of a slot; empty when the slot is unused.</summary>
        public TriggerOp[] this[int slot] => scripts.TryGetValue(slot, out var ops) ? ops : Array.Empty<TriggerOp>();
    }
}
