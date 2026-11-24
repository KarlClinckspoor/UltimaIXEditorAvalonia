using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using U9.Formats.Objects;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    /// <summary>
    /// Making scenes: new camera points, new trigger scripts that chain them, a trigger zone that starts them, and a
    /// generator for a camera that circles an object. Everything goes into fresh script slots and new objects, so
    /// shipped scenes are not touched, and each call is one undo step.
    /// </summary>
    public sealed partial class MapEditor
    {
        /// <summary>How far a new scene's scripts look for their camera points (points); the shipped default is 10.</summary>
        public const int SceneRange = 200;

        /// <summary>The ops that move the camera to the camera point with a link: range, set link, activate, end.</summary>
        public static TriggerOp[] CameraMoveScript(int link, CameraMove move) => new[]
        {
            new TriggerOp(0x1000 | TriggerOp.Range, 0, SceneRange),
            new TriggerOp(0x1000 | TriggerOp.SetLink, 0, (ushort)link),
            new TriggerOp(0x1000 | TriggerOp.Activate, CameraPoint.Type, (ushort)move.B),
            new TriggerOp(0x1000 | TriggerOp.End, 0, 0),
        };

        /// <summary>Link numbers no object of this map's nonfixed file uses, above the highest in use.</summary>
        public List<int> FreeLinks(int count)
        {
            var used = new HashSet<int>(Nonfixed?.Objects.Where(o => !o.Removed).Select(o => o.Link) ?? Enumerable.Empty<int>());
            var list = new List<int>();
            int link = FreeLinkIn(Nonfixed);
            while (list.Count < count) { if (!used.Contains(link)) list.Add(link); link++; }
            return list;
        }

        /// <summary>A camera point's 16-byte property segment: Misc Usecode 1 (the distance) and OO Trigger (two script slots).</summary>
        static byte[] CameraPointSegment(int distanceUnits, int script1, int script2)
        {
            var seg = new byte[16];
            seg[0] = 2;
            seg[1] = PropertySegment.MiscUsecode1;
            seg[2] = PropertySegment.OoTrigger;
            BitConverter.TryWriteBytes(seg.AsSpan(4), (ushort)distanceUnits);
            BitConverter.TryWriteBytes(seg.AsSpan(8), (ushort)script1);
            BitConverter.TryWriteBytes(seg.AsSpan(10), (ushort)script2);
            return seg;
        }

        (int X, int Y, int Z) ObjectSpot(CameraPose pose)
        {
            int x = Math.Clamp((int)MathF.Round(pose.Target.X * MapSpace.ObjectUnitsPerPoint), 0, Space.Width * 128 - 1);
            int y = Math.Clamp((int)MathF.Round(Space.Row(pose.Target.Z) * MapSpace.ObjectUnitsPerPoint), 0, Space.Height * 128 - 1);
            int z = Math.Clamp((int)MathF.Round(pose.Target.Y * MapSpace.HeightUnitsPerPoint * 4) + CameraPoint.PivotDrop, 0, 0xFFFF);
            return (x, y, z);
        }

        /// <summary>Inside an edit: a new camera point where a camera pose is, with a link and its two script slots.</summary>
        MapObject AddCameraPoint(CameraPose pose, int link, int script1 = 0, int script2 = 0)
        {
            var record = TemplateFor(ObjectFileKind.Nonfixed, CameraPoint.Type);
            var (x, y, z) = ObjectSpot(pose);
            int dist = Math.Clamp((int)MathF.Round(pose.Distance * MapSpace.ObjectUnitsPerPoint), 8, 0xFFFF);
            var o = Nonfixed.Add(record, CameraPointSegment(dist, script1, script2), x, y, z);
            Nonfixed.SetLink(o, link);
            o.SetRotation(CameraPoint.RotationFor(pose.Yaw, pose.Pitch));
            return o;
        }

        /// <summary>Inside an edit: a script in a new slot.</summary>
        int AddScript(TriggerOp[] ops)
        {
            int slot = Scripts.FreeSlot();
            SetScript(slot, ops);
            return slot;
        }

        /// <summary>Inside an edit: sets one of an object's OO Trigger script slots (0 or 1).</summary>
        static void SetOoScript(MapObject o, int which, int slot)
        {
            var p = o.Map.PropertiesOf(o).FirstOrDefault(q => q.Id == PropertySegment.OoTrigger)
                    ?? throw new InvalidOperationException($"The object (type {o.Type}, link {o.Link}) has no OO Trigger property to run a script from");
            o.Map.SetProperty(o, p, which == 0 ? slot : p.A, which == 1 ? slot : p.B);
        }

        void RequireScenes()
        {
            if (Scripts == null) throw new InvalidOperationException("No TRIGGERS.FLX");
            if (Nonfixed == null) throw new InvalidOperationException("This map has no nonfixed object file");
        }

        /// <summary>
        /// A new scene: a trigger zone (a copy of a zone's record and property segment, which hold its size) at a
        /// ground position whose script cuts the camera to a new camera point where <paramref name="first"/> is, for
        /// <paramref name="halfSeconds"/>. Walking into the zone plays it; Add move makes it longer.
        /// </summary>
        public SceneObject NewScene(byte[] zoneRecord, byte[] zoneSegment, int mapX, int mapY, int z, CameraPose first, int halfSeconds = 4)
        {
            RequireScenes();
            var seg = new PropertySegment(zoneSegment);
            int k = Array.FindIndex(seg.Ids, id => id == PropertySegment.OoTrigger);
            if (k < 0) throw new ArgumentException("The template zone has no OO Trigger property");
            var zone = Apply(Nonfixed, "new scene", () =>
            {
                int link = FreeLinks(1)[0];
                AddCameraPoint(first, link);
                var zo = Nonfixed.Add(zoneRecord, zoneSegment, mapX, mapY, Math.Clamp(z, 0, 0xFFFF));
                Nonfixed.SetLink(zo, 0);
                int slot = AddScript(CameraMoveScript(link, CameraMove.Make(halfSeconds, false, false, false, false, false)));
                Nonfixed.SetPropertyParam(zo, 2 * k, slot);
                Nonfixed.SetPropertyParam(zo, 2 * k + 1, 0);
                return zo;
            });
            return Objects.Of(zone);
        }

        /// <summary>
        /// Adds a move at the end of a scene: a new camera point where <paramref name="pose"/> is, reached with
        /// <paramref name="move"/> when the scene's last move ends (that move gets "then run the point's first
        /// script", and its point a new script that sends the camera on). One undo step.
        /// </summary>
        public SceneObject AddSceneMove(CameraPath scene, CameraPose pose, CameraMove move)
        {
            RequireScenes();
            var last = scene.Shots.LastOrDefault() ?? throw new ArgumentException("The scene has no moves");
            if (last.Move.RunsFirst || last.Move.RunsSecond)
                throw new InvalidOperationException("The scene's last move already runs a script when it ends");
            var o = Apply(Nonfixed, $"add a move to point {last.Point.Link}'s scene", () => AppendMove(last, pose, move));
            return Objects.Of(o);
        }

        MapObject AppendMove(CameraShot last, CameraPose pose, CameraMove move)
        {
            int link = FreeLinks(1)[0];
            var point = AddCameraPoint(pose, link);
            // the last move now runs its point's first script when it ends ...
            var ops = (TriggerOp[])Scripts[last.Slot].Clone();
            ops[last.OpIndex] = ops[last.OpIndex].WithB(last.Move.B | CameraMove.RunFirst);
            SetScript(last.Slot, ops);
            // ... which sends the camera on to the new point
            SetOoScript(last.Point.Object, 0, AddScript(CameraMoveScript(link, move)));
            return point;
        }

        /// <summary>
        /// A scene that circles an object: <paramref name="count"/> camera points round it at <paramref name="radius"/>
        /// points, <paramref name="height"/> object units above <paramref name="centreZ"/>, each looking at the centre,
        /// and a lap of <paramref name="seconds"/> in straight glides (a last point where the first is closes the lap;
        /// then the camera goes back to the player). Started by a new trigger zone at (mapX, mapY). One undo step.
        /// </summary>
        public SceneObject NewOrbitScene(byte[] zoneRecord, byte[] zoneSegment, int mapX, int mapY, int zoneZ,
                                         float centreX, float centreRow, int centreZ, float radius, int height, int count, float seconds)
        {
            RequireScenes();
            if (count < 3 || count > 64) throw new ArgumentOutOfRangeException(nameof(count), "3 to 64 camera points");
            if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
            var seg = new PropertySegment(zoneSegment);
            int k = Array.FindIndex(seg.Ids, id => id == PropertySegment.OoTrigger);
            if (k < 0) throw new ArgumentException("The template zone has no OO Trigger property");
            int half = Math.Clamp((int)MathF.Round(seconds * 2 / count), 1, 0x7FF);
            var poses = OrbitPoses(centreX, centreRow, centreZ, radius, height, count);
            var zone = Apply(Nonfixed, "new orbit scene", () =>
            {
                var links = FreeLinks(count + 1);
                var points = new List<MapObject>();
                for (int i = 0; i <= count; i++) points.Add(AddCameraPoint(poses[i % count], links[i]));
                // each point's first script glides on to the next; the last point ends the scene
                for (int i = 0; i < count; i++)
                {
                    var next = CameraMove.Make(half, glide: true, linear: true, back: false, runFirst: i + 1 < count, runSecond: false);
                    SetOoScript(points[i], 0, AddScript(CameraMoveScript(links[i + 1], next)));
                }
                var zo = Nonfixed.Add(zoneRecord, zoneSegment, mapX, mapY, Math.Clamp(zoneZ, 0, 0xFFFF));
                Nonfixed.SetLink(zo, 0);
                int start = AddScript(CameraMoveScript(links[0], CameraMove.Make(1, false, false, false, runFirst: true, runSecond: false)));
                Nonfixed.SetPropertyParam(zo, 2 * k, start);
                Nonfixed.SetPropertyParam(zo, 2 * k + 1, 0);
                return zo;
            });
            return Objects.Of(zone);
        }

        /// <summary>Camera poses evenly round a centre (point coordinates, object z), each looking at it.</summary>
        public List<CameraPose> OrbitPoses(float centreX, float centreRow, int centreZ, float radius, int height, int count)
        {
            var list = new List<CameraPose>();
            float pitch = MathF.Atan2(height / (4f * MapSpace.HeightUnitsPerPoint), radius) * 180 / MathF.PI;
            for (int i = 0; i < count; i++)
            {
                float a = i * 2 * MathF.PI / count;            // clockwise from north, as headings go
                float x = centreX + radius * MathF.Sin(a), row = centreRow + radius * MathF.Cos(a);
                var target = new Vector3(x, MapSpace.WorldYFromObject(centreZ + height), Space.WorldZ(row));
                float heading = (i * 360f / count + 180) % 360;   // back towards the centre
                list.Add(new CameraPose(target, heading, pitch, 100 / MapSpace.ObjectUnitsPerPoint));
            }
            return list;
        }
    }
}
