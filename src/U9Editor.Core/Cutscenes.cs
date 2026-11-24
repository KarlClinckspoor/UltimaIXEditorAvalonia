using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using U9.Formats.Objects;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    /// <summary>Where a camera is and how it looks, in the editor's world space (see <see cref="OrbitCamera"/>).</summary>
    public readonly struct CameraPose
    {
        public readonly Vector3 Target;
        /// <summary>Clockwise from north, degrees.</summary>
        public readonly float Yaw;
        /// <summary>Degrees looking down.</summary>
        public readonly float Pitch;
        /// <summary>From the eye to the target, in point spacings.</summary>
        public readonly float Distance;
        public CameraPose(Vector3 target, float yaw, float pitch, float distance) { Target = target; Yaw = yaw; Pitch = pitch; Distance = distance; }

        public static CameraPose Lerp(CameraPose a, CameraPose b, float f)
        {
            float dy = ((b.Yaw - a.Yaw) % 360 + 540) % 360 - 180;    // the short way round
            float yaw = (a.Yaw + dy * f) % 360;
            if (yaw < 0) yaw += 360;
            return new CameraPose(Vector3.Lerp(a.Target, b.Target, f), yaw, a.Pitch + (b.Pitch - a.Pitch) * f, a.Distance + (b.Distance - a.Distance) * f);
        }

        public void ApplyTo(OrbitCamera c)
        {
            c.Target = Target; c.Yaw = Yaw; c.Pitch = Pitch; c.Distance = Distance;
        }

        public override string ToString() => $"({Target.X:0.##}, {Target.Z:0.##}) heading {Yaw:0}° pitch {Pitch:0.#}° distance {Distance * MapSpace.ObjectUnitsPerPoint:0}";
    }

    /// <summary>
    /// A camera point: a nonfixed object of type 2218 that in-engine scenes move the camera to. Its position less
    /// 0x40 in z is the point looked at, its rotation the view, its Misc Usecode 1 property (A) the distance from the
    /// eye in object units.
    /// <para>Angles as u9.exe reads them (0x500820): the rotation matrix it builds from the quaternion is the transpose
    /// of the usual one, M01 = 2(xy + wz); taken as Rz(a)·Rx(b)·Ry(c), the camera turns by a + 180° (a model's front
    /// faces south) and pitches by b; c is ignored. The opening scene's points on Earth face east with b from -2° to
    /// -11° as the camera comes into the Avatar's house, which is read here as looking down.</para>
    /// </summary>
    public sealed class CameraPoint
    {
        public const int Type = 2218;
        public const int PivotDrop = 0x40;
        public MapObject Object { get; }
        public int Link => Object.Link;
        /// <summary>The camera's distance in object units (Misc Usecode 1, A); 0 when the point has none.</summary>
        public int DistanceUnits { get; }
        public CameraPose Pose { get; }

        public CameraPoint(MapObject o, MapSpace space)
        {
            Object = o;
            DistanceUnits = o.Map.PropertiesOf(o).Where(p => p.Id == PropertySegment.MiscUsecode1).Select(p => p.A).FirstOrDefault();
            var (yaw, pitch) = Angles(o.Rotation);
            var target = new Vector3((float)o.PointX, MapSpace.WorldYFromObject(o.Z - PivotDrop), space.WorldZ((float)o.PointRow));
            Pose = new CameraPose(target, yaw, pitch, DistanceUnits / MapSpace.ObjectUnitsPerPoint);
        }

        /// <summary>Heading (clockwise from north) and pitch (down) of a camera point's rotation, in degrees.</summary>
        public static (float Yaw, float Pitch) Angles(Quaternion q)
        {
            q = Quaternion.Normalize(q);
            float m01 = 2 * (q.X * q.Y + q.W * q.Z), m11 = 1 - 2 * (q.X * q.X + q.Z * q.Z), m21 = 2 * (q.Y * q.Z - q.W * q.X);
            float a = MathF.Atan2(-m01, m11) * 180 / MathF.PI;
            float b = MathF.Asin(Math.Clamp(m21, -1f, 1f)) * 180 / MathF.PI;
            float yaw = (180 + a) % 360;
            if (yaw < 0) yaw += 360;
            return (yaw, -b);
        }

        /// <summary>The rotation that <see cref="Angles"/> reads back as this heading and pitch (no roll).</summary>
        public static Quaternion RotationFor(float yaw, float pitch)
        {
            float a = (yaw - 180) * MathF.PI / 180, b = -pitch * MathF.PI / 180;
            // the game's matrix is Rz(a)·Rx(b), the transpose of the usual matrix of the stored quaternion
            return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -b) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -a));
        }
    }

    /// <summary>One camera move of a scene: the point, how it gets there, and the script op that says so.</summary>
    public sealed class CameraShot
    {
        public CameraPoint Point { get; init; }
        public CameraMove Move { get; init; }
        /// <summary>The object whose script sent the move, the script's slot and the op's index in it.</summary>
        public MapObject From { get; init; }
        public int Slot { get; init; }
        public int OpIndex { get; init; }
        /// <summary>When the move starts, from the start of the scene.</summary>
        public int StartMs { get; init; }
        /// <summary>The other ops of the script that sent it (raw), done at the same time.</summary>
        public string Also { get; init; }
        public int EndMs => StartMs + Move.Milliseconds;
        public override string ToString() => $"{StartMs / 1000.0,5:0.0} s  point {Point.Link}: {Move}";
    }

    /// <summary>A scene: the chain of camera moves that one object's script starts.</summary>
    public sealed class CameraPath
    {
        public MapObject Starter { get; init; }
        /// <summary>0 / 1: the starter's first / second OO Trigger script; 2 / 3: its SF Trigger scripts.</summary>
        public int StarterScript { get; init; }
        public int StarterSlot { get; init; }
        public List<CameraShot> Shots { get; } = new List<CameraShot>();
        public int TotalMs => Shots.Count == 0 ? 0 : Shots[^1].EndMs;

        public static string ScriptName(int k) => k switch { 0 => "OO trigger 1", 1 => "OO trigger 2", 2 => "SF trigger 1", _ => "SF trigger 2" };

        /// <summary>
        /// The camera at a time (ms) from the start: each move eases (or goes straight, or cuts) from where the camera
        /// was when it started, until its time is up or the next move starts.
        /// </summary>
        public CameraPose Sample(double ms)
        {
            if (Shots.Count == 0) return default;
            var start = Shots[0].Point.Pose;
            var from = start;
            for (int i = 0; i < Shots.Count; i++)
            {
                var s = Shots[i];
                double until = i + 1 < Shots.Count ? Shots[i + 1].StartMs : double.MaxValue;
                var pose = PoseDuring(s, from, start, Math.Min(ms, until));
                if (ms < until) return pose;
                from = pose;
            }
            return from;
        }

        static CameraPose PoseDuring(CameraShot s, CameraPose from, CameraPose start, double ms)
        {
            var to = s.Move.Returns ? start : s.Point.Pose;
            if (!s.Move.Glides && !s.Move.Returns) return to;
            float f = s.Move.Milliseconds <= 0 ? 1 : (float)Math.Clamp((ms - s.StartMs) / s.Move.Milliseconds, 0, 1);
            if (!s.Move.IsLinear) f = (1 - MathF.Cos(f * MathF.PI)) / 2;
            return CameraPose.Lerp(from, to, f);
        }

        public override string ToString() =>
            $"{(Starter.Type == CameraPoint.Type ? "camera point" : "type " + Starter.Type)} link {Starter.Link} ({Starter.PointX:0.#}, {Starter.PointRow:0.#}), {ScriptName(StarterScript)}: {Shots.Count} moves, {TotalMs / 1000.0:0.#} s";
    }

    /// <summary>
    /// Finds a map's in-engine scenes. A scene is camera moves (op 0x01 sent to camera points) chained by the moves'
    /// "when done, run the point's script" flags; it starts from a script of some other object (the opening on Earth:
    /// a timer in the Avatar's house, link 1504, whose SF trigger moves the camera to point 71, which goes on to 72,
    /// 58 and 59). Scripts are followed as u9.exe runs them: the current link starts as the scripted object's link,
    /// op 0x11 sets it, op 0x2C sets how far away the targets may be (here the nearest point with the link wins if
    /// none is in range).
    /// </summary>
    public static class Cutscenes
    {
        public static List<CameraPath> Find(ObjectMap nonfixed, TriggerScripts scripts, MapSpace space)
        {
            var paths = new List<CameraPath>();
            if (nonfixed == null || scripts == null) return paths;
            var points = nonfixed.Objects.Where(o => !o.Removed && o.Type == CameraPoint.Type).Select(o => new CameraPoint(o, space)).ToList();
            if (points.Count == 0) return paths;
            var reached = new HashSet<MapObject>();
            foreach (var o in nonfixed.Objects)
            {
                if (o.Removed || o.Type == CameraPoint.Type) continue;
                AddPaths(o, nonfixed, scripts, points, paths, reached);
            }
            // chains no other object starts here (started from another map, or by the game's own code)
            foreach (var p in points)
                if (!reached.Contains(p.Object)) AddPaths(p.Object, nonfixed, scripts, points, paths, reached);
            return paths;
        }

        static void AddPaths(MapObject o, ObjectMap map, TriggerScripts scripts, List<CameraPoint> points, List<CameraPath> paths, HashSet<MapObject> reached)
        {
            var slots = ScriptSlots(o);
            for (int k = 0; k < 4; k++)
            {
                if (slots[k] <= 0 || !scripts.IsUsed(slots[k])) continue;
                var path = new CameraPath { Starter = o, StarterScript = k, StarterSlot = slots[k] };
                new Simulation(map, scripts, points, path).Start(o, slots[k]);
                if (path.Shots.Count == 0) continue;
                // an object's scripts often start the same scene (one calls the other): keep it once
                string key = Signature(path);
                if (paths.Any(q => q.Starter == o && Signature(q) == key)) continue;
                paths.Add(path);
                foreach (var s in path.Shots) reached.Add(s.Point.Object);
            }
        }

        static string Signature(CameraPath p) => string.Join(";", p.Shots.Select(s => $"{s.Point.Link}/{s.Move.B}/{s.StartMs}"));

        /// <summary>An object's four script slots: OO Trigger A, B, SF Trigger A, B (0 where it has none).</summary>
        public static int[] ScriptSlots(MapObject o)
        {
            var slots = new int[4];
            foreach (var p in o.Map.PropertiesOf(o))
            {
                if (p.Id == PropertySegment.OoTrigger) { slots[0] = p.A; slots[1] = p.B; }
                else if (p.Id == PropertySegment.SfTrigger) { slots[2] = p.A; slots[3] = p.B; }
            }
            return slots;
        }

        /// <summary>
        /// Runs a scene's scripts in time order: a script's ops happen at once; a camera move replaces the one under way,
        /// and when its time is up (if nothing replaced it) runs the point's scripts its flags name; op 0x28 makes its
        /// targets run their script B &amp; 3 (0, 1 OO, 2, 3 SF) at once.
        /// </summary>
        sealed class Simulation
        {
            public const int RunScript = 0x28;
            const int MaxScripts = 400;
            readonly ObjectMap map;
            readonly TriggerScripts scripts;
            readonly List<CameraPoint> points;
            readonly CameraPath path;
            readonly PriorityQueue<CameraShot, (int Ms, int Seq)> ends = new();
            int seq, runs;
            CameraShot current;

            public Simulation(ObjectMap map, TriggerScripts scripts, List<CameraPoint> points, CameraPath path)
            { this.map = map; this.scripts = scripts; this.points = points; this.path = path; }

            public void Start(MapObject owner, int slot)
            {
                RunAt(owner, slot, 0);
                while (runs < MaxScripts && ends.TryDequeue(out var shot, out var when))
                {
                    if (shot != current) continue;      // replaced before its time was up
                    current = null;
                    var next = ScriptSlots(shot.Point.Object);
                    if (shot.Move.RunsFirst && next[0] > 0) RunAt(shot.Point.Object, next[0], when.Ms);
                    if (shot.Move.RunsSecond && next[1] > 0) RunAt(shot.Point.Object, next[1], when.Ms);
                }
            }

            void RunAt(MapObject owner, int slot, int ms)
            {
                if (++runs > MaxScripts || !scripts.IsUsed(slot)) return;
                var ops = scripts[slot];
                int link = owner.Link, range = TriggerOp.DefaultRange;
                var also = new List<string>();
                var started = new List<(CameraPoint Point, CameraMove Move, int Index)>();
                var calls = new List<(MapObject Owner, int Slot)>();
                for (int i = 0; i < ops.Length; i++)
                {
                    var op = ops[i];
                    if (op.Command == TriggerOp.End) break;
                    if (op.Command == TriggerOp.SetLink) { link = op.B + op.LinkOffset; continue; }
                    if (op.Command == TriggerOp.Range) { range = op.RangeUnits; continue; }
                    int? wanted = op.UsesLink ? link + op.LinkOffset : null;
                    if (op.Command == TriggerOp.Activate && op.TargetType == CameraPoint.Type)
                    {
                        var p = Target(points, owner, wanted, range);
                        if (p != null) { started.Add((p, new CameraMove(op.B), i)); continue; }
                    }
                    if (op.Command == RunScript)
                        foreach (var t in Targets(owner, op.TargetType, wanted, range))
                        {
                            int s = ScriptSlots(t)[op.B & 3];
                            if (s > 0) calls.Add((t, s));
                        }
                    also.Add(op.ToString());
                }
                string alsoText = string.Join(" ", also);
                foreach (var m in started)
                {
                    current = new CameraShot { Point = m.Point, Move = m.Move, From = owner, Slot = slot, OpIndex = m.Index, StartMs = ms, Also = alsoText };
                    path.Shots.Add(current);
                }
                if (started.Count > 0) ends.Enqueue(current, (current.EndMs, ++seq));
                foreach (var c in calls) RunAt(c.Owner, c.Slot, ms);
            }

            /// <summary>The objects an op reaches: of the type (any for 0x1FFF), with the link (any when null), in range of the scripted object.</summary>
            IEnumerable<MapObject> Targets(MapObject owner, int type, int? link, int range)
            {
                foreach (var o in map.Objects)
                {
                    if (o.Removed || (type != TriggerOp.AnyType && o.Type != type) || (link is int l && o.Link != l)) continue;
                    if (Math.Abs(o.MapX - owner.MapX) <= range && Math.Abs(o.MapY - owner.MapY) <= range) yield return o;
                }
            }
        }

        /// <summary>The camera point an op reaches: with the link (any when null), the nearest to the scripted object, preferring those in range.</summary>
        static CameraPoint Target(List<CameraPoint> points, MapObject owner, int? link, int range)
        {
            CameraPoint best = null;
            long bestD = long.MaxValue;
            foreach (var p in points)
            {
                if (link is int l && p.Link != l) continue;
                long dx = p.Object.MapX - owner.MapX, dy = p.Object.MapY - owner.MapY;
                bool inRange = Math.Abs(dx) <= range && Math.Abs(dy) <= range;
                long d = dx * dx + dy * dy + (inRange ? 0 : 1L << 40);
                if (d < bestD) { bestD = d; best = p; }
            }
            if (link == null && bestD >= 1L << 40) return null;   // "any camera point" only counts in range
            return best;
        }
    }
}
