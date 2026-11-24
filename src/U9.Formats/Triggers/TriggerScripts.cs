using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using U9.Formats.Flx;

namespace U9.Formats.Triggers
{
    /// <summary>
    /// One trigger op: (u16 op, u16 a, u16 b). The op's low byte is the command; its high byte's low 5 bits minus 16
    /// are a signed offset added to the script's current link (0x10 = +0, 0x11 = +1, 0x0E = -2); 0 there means the op
    /// is not limited to a link. For most commands A's low 13 bits are the object type the op works on (0x1FFF: any
    /// type) and B its parameter.
    /// <para>How u9.exe runs a script (its trigger interpreter at 0x54F850 in the GOG exe): the current link starts as
    /// the triggering object's link and op 0x11 sets it; op 0x2C sets the search range. A handful of commands are done
    /// by the interpreter itself (0x11, 0x13, 0x14, 0x1F, 0x2C, 0x63 and others); every other command is sent to each
    /// object of the type and link within the range of the triggering object, as an event its type's usecode handles
    /// with B as the parameter (0x01 is "activate": a camera point, type 2218, then moves the camera, see
    /// <see cref="CameraMove"/>).</para>
    /// </summary>
    public readonly struct TriggerOp
    {
        public const int Activate = 0x01, SetLink = 0x11, ChangeMap = 0x1F, Range = 0x2C, End = 0xFF;
        public const int AnyType = 0x1FFF;
        public readonly ushort Op, A, B;
        public TriggerOp(ushort op, ushort a, ushort b) { Op = op; A = a; B = b; }
        public int Command => Op & 0xFF;
        public int LinkOffset => (Op >> 8) - 16;
        /// <summary>Whether the op only reaches objects with the current link plus <see cref="LinkOffset"/>.</summary>
        public bool UsesLink => ((Op >> 8) & 0x1F) != 0;
        /// <summary>The object type the op works on (<see cref="AnyType"/> for any).</summary>
        public int TargetType => A & 0x1FFF;
        /// <summary>For op 0x2C: the search range in object units (B x 128, or B &amp; 0x7FFF when its top bit is set; 0x500 when that is 0).</summary>
        public int RangeUnits
        {
            get
            {
                if (Command != Range) return 0;
                int r = (B & 0x8000) != 0 ? B & 0x7FFF : B << 7;
                return r != 0 ? r : DefaultRange;
            }
        }
        public const int DefaultRange = 0x500;
        public TriggerOp WithB(int b) => new TriggerOp(Op, A, (ushort)b);
        /// <summary>The command's meaning as far as it is known (null when not).</summary>
        public string Name => Command switch
        {
            Activate => "activate",
            SetLink => "set link",
            ChangeMap => "change map",
            Range => "range",
            0x28 => "run the objects' script B & 3",
            End => "end",
            _ => null,
        };

        /// <summary>What the op does, for a comment: its name, the type and link it reaches, and what B means.</summary>
        public string Describe()
        {
            string link = UsesLink ? $"link {(LinkOffset >= 0 ? "+" : "")}{LinkOffset}" : "any link";
            string type = TargetType == AnyType ? "any type" : $"type {TargetType}";
            return Command switch
            {
                End => "end",
                SetLink => $"set link {B + LinkOffset}",
                Range => $"range {RangeUnits / 128.0:0.#} points",
                ChangeMap => $"change map to {((B & 0xFF) == 0 ? "this map" : "map " + (B & 0xFF))}, arrival type {A}",
                Activate when TargetType == 2218 => $"activate camera point ({link}): {new CameraMove(B)}",
                Activate => $"activate {type}, {link}, B {B}",
                0x28 => $"{type}, {link} run script {B & 3} (OO 1, OO 2, SF 1, SF 2)",
                _ => $"op 0x{Command:X2} on {type}, {link}, B {B}",
            };
        }
        public override string ToString() => $"{Op:x4}:{A:x},{B:x}";
    }

    /// <summary>
    /// The parameter (B) of op 0x01 sent to a camera point (type 2218), as its usecode reads it (u9.exe 0x633410):
    /// the camera goes to the point's position (its z less 0x40) looking along the point's rotation, at the distance
    /// in the point's Misc Usecode 1 property (A, object units), over the low 11 bits x 500 ms. 0x8000 glides there
    /// from where the camera is (eased with a cosine, or straight with 0x0800); without it the camera cuts there and
    /// waits. 0x4000 glides back to the view the camera had before the scene. When the time is up, 0x2000 runs the
    /// point's first OO Trigger script and 0x1000 its second; a script that moves the camera on keeps the scene going,
    /// otherwise the camera returns to the player.
    /// </summary>
    public readonly struct CameraMove
    {
        public const int Glide = 0x8000, ReturnToPlayer = 0x4000, RunFirst = 0x2000, RunSecond = 0x1000, Linear = 0x0800;
        public readonly int B;
        public CameraMove(int b) { B = b & 0xFFFF; }
        public static CameraMove Make(int halfSeconds, bool glide, bool linear, bool back, bool runFirst, bool runSecond) =>
            new CameraMove((Math.Clamp(halfSeconds, 0, 0x7FF)) | (glide ? Glide : 0) | (linear ? Linear : 0) | (back ? ReturnToPlayer : 0)
                           | (runFirst ? RunFirst : 0) | (runSecond ? RunSecond : 0));
        public int HalfSeconds => B & 0x7FF;
        public int Milliseconds => HalfSeconds * 500;
        public bool Glides => (B & Glide) != 0;
        public bool IsLinear => (B & Linear) != 0;
        public bool Returns => (B & ReturnToPlayer) != 0;
        public bool RunsFirst => (B & RunFirst) != 0;
        public bool RunsSecond => (B & RunSecond) != 0;
        public override string ToString() =>
            $"{(Returns ? "back to the player" : Glides ? (IsLinear ? "glide (straight)" : "glide") : "cut")} {Milliseconds / 1000.0:0.#} s"
            + (RunsFirst ? ", then its first script" : "") + (RunsSecond ? ", then its second script" : "");
    }

    /// <summary>
    /// static/TRIGGERS.FLX: each record is a list of 6-byte <see cref="TriggerOp"/>s ending with op 0x10FF.
    /// Known commands (worked out from the shipped maps):
    /// <list type="bullet">
    /// <item>0x1F change map: B's low byte is the destination map (0 = this map), B's high byte flags (0x01, 0x40,
    /// 0x80; meaning unknown), A the type of the arrival marker (0 = any).</item>
    /// <item>0x11 set link: B (plus the op's offset) becomes the current link (before a 0x1F, the arrival marker's link).</item>
    /// <item>0x2C range: how far from the triggering object the following ops look for their objects (see <see cref="TriggerOp.RangeUnits"/>).</item>
    /// <item>0x01 activate: sends the objects the "activate" event with B (camera points: <see cref="CameraMove"/>).</item>
    /// </list>
    /// </summary>
    public sealed class TriggerScripts
    {
        readonly Dictionary<int, TriggerOp[]> scripts = new Dictionary<int, TriggerOp[]>();
        readonly HashSet<int> changed = new HashSet<int>();
        public int Count { get; private set; }
        /// <summary>The file it was read from.</summary>
        public string Path { get; private set; }
        public bool HasChanges => changed.Count > 0;

        public static TriggerScripts Load(string path)
        {
            var t = new TriggerScripts { Path = path };
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

        public bool IsUsed(int slot) => scripts.ContainsKey(slot);

        /// <summary>Replaces a slot's ops (null or empty frees it). Saved with <see cref="Save"/>.</summary>
        public void Set(int slot, TriggerOp[] ops)
        {
            if (slot <= 0 || slot > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(slot));
            if (ops == null || ops.Length == 0) scripts.Remove(slot); else scripts[slot] = (TriggerOp[])ops.Clone();
            if (slot >= Count) Count = slot + 1;
            changed.Add(slot);
        }

        /// <summary>An unused slot for a new script: the first one after the highest used slot, so new scripts never sit among the shipped ones.</summary>
        public int FreeSlot()
        {
            int max = 0;
            foreach (int k in scripts.Keys) max = Math.Max(max, k);
            return max + 1;
        }

        /// <summary>
        /// A script as text, one op a line: the op in hex, A and B in decimal, and a comment (after ';') saying what it
        /// does. The closing end op is left out; <see cref="Parse"/> adds it back.
        /// </summary>
        public static string Format(IEnumerable<TriggerOp> ops)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var op in ops)
            {
                if (op.Command == TriggerOp.End) break;
                sb.Append($"{op.Op:X4} {op.A,5} {op.B,5}   ; {op.Describe()}\n");
            }
            return sb.ToString();
        }

        /// <summary>Reads <see cref="Format"/>'s text back: blank lines and comments are skipped, an end op is added.</summary>
        public static TriggerOp[] Parse(string text)
        {
            var list = new List<TriggerOp>();
            int n = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                n++;
                string line = raw;
                int c = line.IndexOf(';');
                if (c >= 0) line = line.Substring(0, c);
                line = line.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) throw new FormatException($"line {n}: three numbers wanted (op in hex, A, B)");
                if (!ushort.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out ushort op)) throw new FormatException($"line {n}: '{parts[0]}' is not a hex op");
                if (!ushort.TryParse(parts[1], out ushort a)) throw new FormatException($"line {n}: A '{parts[1]}' is not 0 to 65535");
                if (!ushort.TryParse(parts[2], out ushort b)) throw new FormatException($"line {n}: B '{parts[2]}' is not 0 to 65535");
                if ((op & 0xFF) == TriggerOp.End) break;
                list.Add(new TriggerOp(op, a, b));
            }
            if (list.Count > 84) throw new FormatException("a script holds at most 84 ops (the game reads 85 with the end)");
            list.Add(new TriggerOp(0x1000 | TriggerOp.End, 0, 0));
            return list.ToArray();
        }

        /// <summary>The usual map change script: set the arrival link, change map, end.</summary>
        public static TriggerOp[] ChangeMapScript(int destinationLink, int markerType, int destinationMap, int flags = 0) => new[]
        {
            new TriggerOp(0x1000 | TriggerOp.SetLink, (ushort)markerType, (ushort)destinationLink),
            new TriggerOp(0x1000 | TriggerOp.ChangeMap, (ushort)markerType, (ushort)((destinationMap & 0xFF) | ((flags & 0xFF) << 8))),
            new TriggerOp(0x1000 | TriggerOp.End, 0, 0),
        };

        /// <summary>Writes the changed slots into the archive at <paramref name="path"/> (default: the file read), keeping the rest as it was.</summary>
        public void Save(string path = null)
        {
            path ??= Path;
            using (var a = FlxArchive.Open(Path))
            {
                if (Count > a.Count) a.Resize(Count);
                foreach (int slot in changed)
                {
                    var ops = this[slot];
                    var r = new byte[ops.Length * 6];
                    for (int k = 0; k < ops.Length; k++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(k * 6), ops[k].Op);
                        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(k * 6 + 2), ops[k].A);
                        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(k * 6 + 4), ops[k].B);
                    }
                    a.Write(slot, r.Length > 0 ? r : null);
                }
                a.Save(path);       // over the source it goes through a temporary file
            }
            changed.Clear();
        }
    }
}
