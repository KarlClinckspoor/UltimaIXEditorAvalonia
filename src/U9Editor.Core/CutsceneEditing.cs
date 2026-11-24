using System;
using System.Linq;
using U9.Formats.Objects;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    public sealed partial class MapEditor
    {
        /// <summary>Raised after an edit, undo or redo that changed trigger scripts.</summary>
        public event Action ScriptsEdited;

        /// <summary>
        /// Changes how a scene's camera move goes (its duration and flags): the B of the op in its script, one undo
        /// step. Every object that runs that script gets the change.
        /// </summary>
        public void SetCameraMove(CameraShot shot, CameraMove move)
        {
            if (Scripts == null) throw new InvalidOperationException("No TRIGGERS.FLX");
            var before = Scripts[shot.Slot];
            if (shot.OpIndex < 0 || shot.OpIndex >= before.Length) throw new InvalidOperationException("The script has changed since the scene was read");
            if (before[shot.OpIndex].B == move.B) return;
            var after = (TriggerOp[])before.Clone();
            after[shot.OpIndex] = before[shot.OpIndex].WithB(move.B);
            Scripts.Set(shot.Slot, after);
            undo.Add(new Step { Description = $"camera move to point {shot.Point.Link}", Scripts = new() { (shot.Slot, before, after) } });
            redo.Clear();
            scriptVersion++;
            ScriptsEdited?.Invoke();
        }

        /// <summary>
        /// Puts a camera point where a camera is: its position (the looked-at point, raised by 0x40), its rotation from
        /// the heading and pitch, and its distance (Misc Usecode 1) when the point has that property. One undo step.
        /// </summary>
        public void SetCameraPoint(SceneObject point, CameraPose pose)
        {
            var o = point.Source;
            int mapX = (int)MathF.Round(pose.Target.X * MapSpace.ObjectUnitsPerPoint);
            int mapY = (int)MathF.Round(Space.Row(pose.Target.Z) * MapSpace.ObjectUnitsPerPoint);
            int z = Math.Clamp((int)MathF.Round(pose.Target.Y * MapSpace.HeightUnitsPerPoint * 4) + CameraPoint.PivotDrop, 0, 0xFFFF);
            var q = CameraPoint.RotationFor(pose.Yaw, pose.Pitch);
            var dist = o.Map.PropertiesOf(o).FirstOrDefault(p => p.Id == PropertySegment.MiscUsecode1);
            int units = Math.Clamp((int)MathF.Round(pose.Distance * MapSpace.ObjectUnitsPerPoint), 8, 0xFFFF);
            Apply(o.Map, $"camera point {o.Link} from the view", () =>
            {
                o.MoveTo(mapX, mapY, z);
                o.SetRotation(q);
                if (dist != null && dist.A != units) o.Map.SetProperty(o, dist, units, dist.B);
                return 0;
            });
        }

        /// <summary>Replaces a script's ops (one undo step); every object running that script gets them.</summary>
        public void SetScriptOps(int slot, TriggerOp[] ops)
        {
            if (Scripts == null) throw new InvalidOperationException("No TRIGGERS.FLX");
            if (slot <= 0) throw new ArgumentOutOfRangeException(nameof(slot));
            var before = Scripts.IsUsed(slot) ? Scripts[slot] : Array.Empty<TriggerOp>();
            if (before.SequenceEqual(ops)) return;
            Scripts.Set(slot, ops);
            undo.Add(new Step { Description = $"script {slot}", Scripts = new() { (slot, before, ops) } });
            redo.Clear();
            scriptVersion++;
            ScriptsEdited?.Invoke();
        }

        void RaiseScriptsEdited() => ScriptsEdited?.Invoke();
    }
}
