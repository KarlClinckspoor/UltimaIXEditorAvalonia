using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>
    /// Object editing on one map: moves, turns, copies and deletes go to the map's fixed and nonfixed files as
    /// undoable edits, and the scene (marker positions, model instances) follows. Saving writes the changed files.
    /// </summary>
    public sealed class MapEditor
    {
        public int MapNumber { get; }
        public ObjectMap Fixed { get; }
        public ObjectMap Nonfixed { get; }
        public SceneObjects Objects { get; }
        public MapSpace Space { get; private set; }
        public TerrainRaster Raster { get; private set; }
        /// <summary>Null when models are not shown (no sappear.flx).</summary>
        public ModelScene Models { get; set; }

        sealed class Step
        {
            public ObjectMap Map;
            public ObjectEdit Edit;
        }

        readonly List<Step> undo = new List<Step>(), redo = new List<Step>();
        int fixedVersion, nonfixedVersion, fixedSaved, nonfixedSaved;

        /// <summary>
        /// Objects changed. The flag is true when objects appeared or went (the model instances were regrouped and
        /// every marker must go up again); otherwise only the listed objects moved or turned.
        /// </summary>
        public event Action<IReadOnlyList<SceneObject>, bool> Changed;

        public MapEditor(int mapNumber, ObjectMap fixedObjects, ObjectMap nonfixedObjects, SceneObjects objects, MapSpace space, TerrainRaster raster)
        {
            MapNumber = mapNumber;
            Fixed = fixedObjects;
            Nonfixed = nonfixedObjects;
            Objects = objects;
            Space = space;
            Raster = raster;
        }

        /// <summary>The map was opened again: new terrain, maybe a different orientation.</summary>
        public void Rebind(MapSpace space, TerrainRaster raster)
        {
            Space = space;
            Raster = raster;
            foreach (var o in Objects.All) Objects.Refresh(o, space);
        }

        public bool FixedChanged => fixedVersion != fixedSaved;
        public bool NonfixedChanged => nonfixedVersion != nonfixedSaved;
        public bool HasChanges => FixedChanged || NonfixedChanged;
        public string UndoDescription => undo.Count > 0 ? undo[^1].Edit.Description : null;
        public string RedoDescription => redo.Count > 0 ? redo[^1].Edit.Description : null;

        // ---- positions

        /// <summary>Object height (object units) of the terrain at a map position in object units.</summary>
        public int GroundZ(float mapX, float mapY) =>
            (int)MathF.Round(Raster.HeightAt(mapX / MapSpace.ObjectUnitsPerPoint, mapY / MapSpace.ObjectUnitsPerPoint) * 4f);

        /// <summary>Turn about the vertical, in degrees counter-clockwise seen from above (0 = model's x axis to the east).</summary>
        public static float Yaw(Quaternion q)
        {
            var x = Vector3.Transform(Vector3.UnitX, q);
            return MathF.Atan2(x.Y, x.X) * 180f / MathF.PI;
        }

        /// <summary>The rotation turned by <paramref name="degrees"/> about the vertical (counter-clockwise seen from above).</summary>
        public static Quaternion Turned(Quaternion q, float degrees) =>
            Quaternion.Normalize(Quaternion.Concatenate(q, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f)));

        // ---- edits

        T Apply<T>(ObjectMap map, string description, Func<T> action)
        {
            map.BeginEdit(description);
            T result;
            try { result = action(); }
            catch
            {
                var partial = map.EndEdit();
                if (partial != null) map.Undo(partial);
                throw;
            }
            var e = map.EndEdit();
            if (e != null)
            {
                undo.Add(new Step { Map = map, Edit = e });
                redo.Clear();
                Bump(map, +1);
                Publish(e);
            }
            return result;
        }

        void Bump(ObjectMap map, int d)
        {
            if (map == Fixed) fixedVersion += d; else nonfixedVersion += d;
        }

        void Publish(ObjectEdit e)
        {
            var touched = new List<SceneObject>();
            bool structural = false;
            foreach (var mo in e.Objects)
            {
                var so = Objects.Of(mo);
                if (so == null) { so = Objects.Add(mo, Space); structural = true; }
                Objects.Refresh(so, Space);
                if (so.Hidden != mo.Removed) { so.Hidden = mo.Removed; structural = true; }
                touched.Add(so);
            }
            if (Models != null)
            {
                if (structural) Models.RebuildInstances(Objects, Space);
                else foreach (var so in touched) Models.SetInstance(so, ModelScene.Placement(so.Source, Space));
            }
            Changed?.Invoke(touched, structural);
        }

        /// <summary>Moves (and optionally turns) an object; positions in object units across the map.</summary>
        public void Move(SceneObject o, int mapX, int mapY, int z, Quaternion? rotation = null)
        {
            mapX = Math.Clamp(mapX, 0, o.Source.Map.Width * ObjectMap.ChunkUnits - 1);
            mapY = Math.Clamp(mapY, 0, o.Source.Map.Height * ObjectMap.ChunkUnits - 1);
            z = Math.Clamp(z, 0, 0xFFFF);
            if (!o.Source.Map.CanLeaveChunk(o.Source) &&
                (mapX / ObjectMap.ChunkUnits != o.Source.ChunkX / ObjectMap.ChunkUnits || mapY / ObjectMap.ChunkUnits != o.Source.ChunkY / ObjectMap.ChunkUnits))
                throw new InvalidOperationException("This object's properties chain on to another segment, so it can only move inside its 32-point chunk.");
            Apply(o.Source.Map, rotation != null ? "move and turn" : "move", () =>
            {
                if (mapX != o.Source.MapX || mapY != o.Source.MapY || z != o.Source.Z) o.Source.MoveTo(mapX, mapY, z);
                if (rotation is { } q) o.Source.SetRotation(q);
                return 0;
            });
        }

        public void Rotate(SceneObject o, Quaternion q) => Apply(o.Source.Map, "turn", () => { o.Source.SetRotation(q); return 0; });

        public SceneObject Duplicate(SceneObject o, int mapX, int mapY, int z)
        {
            mapX = Math.Clamp(mapX, 0, o.Source.Map.Width * ObjectMap.ChunkUnits - 1);
            mapY = Math.Clamp(mapY, 0, o.Source.Map.Height * ObjectMap.ChunkUnits - 1);
            var copy = Apply(o.Source.Map, "copy", () => o.Source.Map.Duplicate(o.Source, mapX, mapY, Math.Clamp(z, 0, 0xFFFF)));
            return Objects.Of(copy);
        }

        public void Remove(SceneObject o) => Apply(o.Source.Map, "delete", () => { o.Source.Map.Remove(o.Source); return 0; });

        /// <summary>Takes back the last edit; returns its description, or null if there was none.</summary>
        public string Undo()
        {
            if (undo.Count == 0) return null;
            var s = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            s.Map.Undo(s.Edit);
            redo.Add(s);
            Bump(s.Map, -1);
            Publish(s.Edit);
            return s.Edit.Description;
        }

        public string Redo()
        {
            if (redo.Count == 0) return null;
            var s = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            s.Map.Redo(s.Edit);
            undo.Add(s);
            Bump(s.Map, +1);
            Publish(s.Edit);
            return s.Edit.Description;
        }

        /// <summary>
        /// Writes the changed files. The first time a file is overwritten, the original is kept beside it as
        /// name.orig. Returns the paths written.
        /// </summary>
        public List<string> Save(string fixedPath, string nonfixedPath)
        {
            var written = new List<string>();
            if (FixedChanged && Fixed != null && fixedPath != null) { Write(Fixed, fixedPath); written.Add(fixedPath); fixedSaved = fixedVersion; }
            if (NonfixedChanged && Nonfixed != null && nonfixedPath != null) { Write(Nonfixed, nonfixedPath); written.Add(nonfixedPath); nonfixedSaved = nonfixedVersion; }
            return written;
        }

        static void Write(ObjectMap m, string path)
        {
            string backup = path + ".orig";
            if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
            string temp = path + ".new";
            File.WriteAllBytes(temp, m.Data);
            File.Move(temp, path, true);
        }
    }
}
