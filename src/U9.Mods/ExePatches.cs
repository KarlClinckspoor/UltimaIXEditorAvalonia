using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace U9.Mods
{
    /// <summary>One change to u9.exe: at a file offset, the bytes expected and the bytes written (same length).</summary>
    public sealed class ExePatch
    {
        public int Offset;
        public byte[] Old, New;
        public string Note = "";
        public int End => Offset + New.Length;
        public override string ToString() => (New.Length <= 16 ? $"0x{Offset:X6}: {Convert.ToHexString(Old)} -> {Convert.ToHexString(New)}" : $"0x{Offset:X6}: {New.Length} bytes") +
                                             (Note.Length > 0 ? "  " + Note : "");
    }

    /// <summary>
    /// Patches to the executable: found by comparing two images, checked and applied, and kept clear of the code U9GL
    /// checks before it hooks the game. The image's sections map virtual addresses to file offsets.
    /// </summary>
    public static class ExePatches
    {
        /// <summary>
        /// U9GL's build signature: code it reads before hooking the game (u9gl/src/common/buildid.c, U9GL 0.6.0). A patch
        /// here would make U9GL refuse the game, so mods may not touch these bytes.
        /// </summary>
        public static readonly (uint Va, int Length, string What)[] U9glSignature =
        {
            (0x004163F0, 3, "Actor_NeedsRebuild"),
            (0x0043CB5C, 6, "Actor_ResetTransientState"),
            (0x004DD0F0, 6, "Actor_Tick_PruneRefs entry"),
            (0x0064975D, 6, "SpellTeleport_Tick (F-5 site)"),
            (0x00649747, 5, "SpellTeleport_Tick (second F-5 site)"),
            (0x004F6250, 1, "Actor_Create"),
        };

        /// <summary>The patches that turn <paramref name="original"/> into <paramref name="edited"/> (same length): runs of changed bytes, joined when under 8 bytes apart.</summary>
        public static List<ExePatch> Diff(byte[] original, byte[] edited)
        {
            if (original.Length != edited.Length) throw new InvalidOperationException("The executable changed length; only same-length patches can go into a mod.");
            var list = new List<ExePatch>();
            int i = 0, n = original.Length;
            while (i < n)
            {
                int d = original.AsSpan(i).CommonPrefixLength(edited.AsSpan(i));
                i += d;
                if (i >= n) break;
                int start = i, last = i;
                while (i < n && i - last < 8)
                {
                    if (original[i] != edited[i]) last = i;
                    i++;
                }
                int len = last - start + 1;
                list.Add(new ExePatch { Offset = start, Old = original.AsSpan(start, len).ToArray(), New = edited.AsSpan(start, len).ToArray() });
                i = last + 1;
            }
            return list;
        }

        /// <summary>Writes patches into an image, after checking each finds the bytes it expects.</summary>
        public static void Apply(byte[] image, IEnumerable<ExePatch> patches)
        {
            foreach (var p in patches)
            {
                if (p.Offset < 0 || p.End > image.Length) throw new InvalidOperationException($"A patch at 0x{p.Offset:X} lies outside the executable.");
                if (!image.AsSpan(p.Offset, p.Old.Length).SequenceEqual(p.Old) && !image.AsSpan(p.Offset, p.New.Length).SequenceEqual(p.New))
                    throw new InvalidOperationException($"The executable does not have the bytes a patch expects at 0x{p.Offset:X}.");
            }
            foreach (var p in patches) p.New.CopyTo(image, p.Offset);
        }

        /// <summary>A patch's clashes with U9GL's signature, as text (empty when clear).</summary>
        public static List<string> U9glClashes(byte[] image, IEnumerable<ExePatch> patches)
        {
            var clashes = new List<string>();
            var sites = U9glSignature.Select(s => (Offset: FileOffset(image, s.Va), s.Length, s.What)).Where(s => s.Offset >= 0).ToList();
            foreach (var p in patches)
                foreach (var s in sites)
                    if (p.Offset < s.Offset + s.Length && s.Offset < p.End)
                        clashes.Add($"the patch at 0x{p.Offset:X} touches U9GL's check of {s.What}");
            return clashes;
        }

        /// <summary>The file offset of a virtual address in a PE image, or -1.</summary>
        public static int FileOffset(byte[] image, uint va)
        {
            if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z') return -1;
            int pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
            if (pe < 0 || pe + 24 > image.Length || image[pe] != 'P' || image[pe + 1] != 'E') return -1;
            int sections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
            uint imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe + 24 + 28));
            int table = pe + 24 + optSize;
            uint rva = va - imageBase;
            for (int k = 0; k < sections; k++)
            {
                int s = table + 40 * k;
                if (s + 40 > image.Length) break;
                uint vsize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 8)), vaddr = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 12));
                uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 16)), raw = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 20));
                if (rva >= vaddr && rva < vaddr + Math.Max(vsize, rawSize) && rva - vaddr < rawSize) return (int)(raw + rva - vaddr);
            }
            return -1;
        }

        /// <summary>Sets the large-address-aware flag (as U9GL's u9-laa.exe has it), so the game may use 4 GB on a 64-bit Windows.</summary>
        public static void SetLargeAddressAware(byte[] image)
        {
            int pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
            if (pe < 0 || pe + 24 > image.Length || image[pe] != 'P' || image[pe + 1] != 'E') throw new InvalidOperationException("u9.exe is not a PE image.");
            int at = pe + 4 + 18;
            ushort c = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), (ushort)(c | 0x20));
        }
    }
}
