using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    /// <summary>
    /// Object editing on one map: moves, turns, copies and deletes go to the map's fixed and nonfixed files as
    /// undoable edits, and the scene (marker positions, model instances) follows. Saving writes the changed files.
    /// </summary>
    public sealed partial class MapEditor
    {
        public int MapNumber { get; }
        public ObjectMap Fixed { get; }
        public ObjectMap Nonfixed { get; }
        public SceneObjects Objects { get; }
        public MapSpace Space { get; private set; }
        public TerrainRaster Raster { get; private set; }
        /// <summary>Null when models are not shown (no sappear.flx).</summary>
        public ModelScene Models { get; set; }

        /// <summary>TRIGGERS.FLX, shared by every map's editor; null when the game has none.</summary>
        public TriggerScripts Scripts { get; set; }
        /// <summary>TYPES.DAT, for new objects' default models.</summary>
        public TypeTable Types { get; set; }
        /// <summary>Objects on the game's other maps, for new objects of a type this map has none of; may be null.</summary>
        public ObjectTemplates Templates { get; set; }

        sealed class Step
        {
            public string Description;
            /// <summary>The object edit; null for a terrain stroke.</summary>
            public ObjectMap Map;
            public ObjectEdit Edit;
            /// <summary>A terrain stroke, and the objects that went up or down with the ground.</summary>
            public TerrainChange Terrain;
            public List<(ObjectMap Map, ObjectEdit Edit)> Follow;
            /// <summary>The terrain header's water level, as stored (quarter point heights): before and after.</summary>
            public (int Before, int After)? Water;
            /// <summary>Script slots the step changed: ops before and after (empty = unused).</summary>
            public List<(int Slot, TriggerOp[] Before, TriggerOp[] After)> Scripts;
        }

        readonly List<Step> undo = new List<Step>(), redo = new List<Step>();
        int fixedVersion, nonfixedVersion, fixedSaved, nonfixedSaved, scriptVersion, scriptSaved;
        List<(int Slot, TriggerOp[] Before, TriggerOp[] After)> pendingScripts;

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
        public bool ScriptsChanged => scriptVersion != scriptSaved;
        public bool HasChanges => FixedChanged || NonfixedChanged || ScriptsChanged || TerrainChanged;
        public string UndoDescription => undo.Count > 0 ? undo[^1].Description : null;
        public string RedoDescription => redo.Count > 0 ? redo[^1].Description : null;

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
            pendingScripts = new List<(int, TriggerOp[], TriggerOp[])>();
            T result;
            try { result = action(); }
            catch
            {
                var partial = map.EndEdit();
                if (partial != null) map.Undo(partial);
                foreach (var sc in Enumerable.Reverse(pendingScripts)) Scripts.Set(sc.Slot, sc.Before);
                pendingScripts = null;
                throw;
            }
            var e = map.EndEdit();
            var scripts = pendingScripts;
            pendingScripts = null;
            if (e != null)
            {
                undo.Add(new Step { Description = description, Map = map, Edit = e, Scripts = scripts.Count > 0 ? scripts : null });
                redo.Clear();
                Bump(map, +1);
                if (scripts.Count > 0) scriptVersion++;
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
            if (InStroke) EndStroke();      // a stroke under way is the last edit
            if (undo.Count == 0) return null;
            var s = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            if (s.Follow != null)
                foreach (var f in Enumerable.Reverse(s.Follow)) { f.Map.Undo(f.Edit); Bump(f.Map, -1); Publish(f.Edit); }
            if (s.Terrain != null) { ApplyTerrain(s.Terrain, after: false); terrainVersion--; }
            if (s.Water is { } wu) { SetWater(wu.Before); terrainVersion--; }
            if (s.Map != null)
            {
                s.Map.Undo(s.Edit);
                Bump(s.Map, -1);
                Publish(s.Edit);
            }
            if (s.Scripts != null) { foreach (var sc in Enumerable.Reverse(s.Scripts)) Scripts.Set(sc.Slot, sc.Before); scriptVersion--; }
            redo.Add(s);
            return s.Description;
        }

        public string Redo()
        {
            if (InStroke) EndStroke();
            if (redo.Count == 0) return null;
            var s = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            if (s.Scripts != null) { foreach (var sc in s.Scripts) Scripts.Set(sc.Slot, sc.After); scriptVersion++; }
            if (s.Map != null)
            {
                s.Map.Redo(s.Edit);
                Bump(s.Map, +1);
                Publish(s.Edit);
            }
            if (s.Terrain != null) { ApplyTerrain(s.Terrain, after: true); terrainVersion++; }
            if (s.Water is { } wr) { SetWater(wr.After); terrainVersion++; }
            if (s.Follow != null)
                foreach (var f in s.Follow) { f.Map.Redo(f.Edit); Bump(f.Map, +1); Publish(f.Edit); }
            undo.Add(s);
            return s.Description;
        }

        // ---- new objects and map links

        /// <summary>
        /// A record for a new object of a type: a copy of one already in the map's file of that kind (flags, model and
        /// such as the game set them; position, link and properties are cleared or replaced), else a copy of one on another
        /// map (<see cref="Templates"/>), else a fresh one with no
        /// rotation and, nonfixed, the type's default model.
        /// </summary>
        public byte[] TemplateFor(ObjectFileKind kind, int type)
        {
            var map = kind == ObjectFileKind.Fixed ? Fixed : Nonfixed;
            if (map == null) return null;
            var like = map.Objects.FirstOrDefault(o => o.Type == type);
            var r = like?.Raw ?? Templates?.Find(kind, type);
            if (r != null && r.Length != map.RecordSize) r = null;
            if (r == null)
            {
                r = new byte[map.RecordSize];
                BitConverter.TryWriteBytes(r.AsSpan(0x0A), (ushort)type);
                BitConverter.TryWriteBytes(r.AsSpan(0x12), (short)32767);
                if (kind == ObjectFileKind.Nonfixed && Types != null) BitConverter.TryWriteBytes(r.AsSpan(0x18), (ushort)Math.Max(0, Types[type].DefaultModel));
            }
            if (kind == ObjectFileKind.Nonfixed) BitConverter.TryWriteBytes(r.AsSpan(0x1A), (ushort)0);
            return r;
        }

        /// <summary>
        /// Which file a new object of a type goes in: the one that already holds objects of that type in this map,
        /// else nonfixed for things that can be picked up (weight under 254), fixed for the rest.
        /// </summary>
        public ObjectFileKind KindFor(int type)
        {
            int f = Fixed?.Objects.Count(o => o.Type == type) ?? 0, n = Nonfixed?.Objects.Count(o => o.Type == type) ?? 0;
            if (f == n && Templates != null) (f, n) = Templates.Count(type);
            if (f != n) return f > n ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;
            if (Nonfixed == null) return ObjectFileKind.Fixed;
            if (Fixed == null) return ObjectFileKind.Nonfixed;
            return Types != null && Types[type].Weight >= 254 ? ObjectFileKind.Fixed : ObjectFileKind.Nonfixed;
        }

        /// <summary>Places a new object of a type; position across the map and height in object units.</summary>
        public SceneObject Place(ObjectFileKind kind, int type, int mapX, int mapY, int z)
        {
            var map = kind == ObjectFileKind.Fixed ? Fixed : Nonfixed;
            if (map == null) throw new InvalidOperationException($"This map has no {(kind == ObjectFileKind.Fixed ? "fixed" : "nonfixed")} object file");
            var record = TemplateFor(kind, type);
            mapX = Math.Clamp(mapX, 0, map.Width * ObjectMap.ChunkUnits - 1);
            mapY = Math.Clamp(mapY, 0, map.Height * ObjectMap.ChunkUnits - 1);
            var o = Apply(map, "place", () => map.Add(record, null, mapX, mapY, Math.Clamp(z, 0, 0xFFFF)));
            return Objects.Of(o);
        }

        /// <summary>
        /// A link number no object in this map's nonfixed file uses: one above the highest ordinary link. Values from
        /// 0xF000 up are passed over, since some objects carry 65535 (a "no link" mark, it seems) and a script's link
        /// offset could reach the neighbours of a number.
        /// </summary>
        public int FreeLink() => FreeLinkIn(Nonfixed);

        /// <summary><see cref="FreeLink"/> for any nonfixed file (null: 1).</summary>
        public static int FreeLinkIn(ObjectMap nonfixed)
        {
            var used = new HashSet<int>(nonfixed?.Objects.Select(o => o.Link) ?? Enumerable.Empty<int>());
            int link = used.Where(l => l < 0xF000).DefaultIfEmpty(0).Max() + 1;
            while (used.Contains(link)) link++;
            return link;
        }

        void SetScript(int slot, TriggerOp[] ops)
        {
            var before = Scripts.IsUsed(slot) ? Scripts[slot] : Array.Empty<TriggerOp>();
            Scripts.Set(slot, ops);
            pendingScripts.Add((slot, before, ops));
        }

        /// <summary>The parameter index of the zone's property that names a trigger slot, or -1.</summary>
        static int TriggerParam(MapObject zone, int slot)
        {
            var p = zone.Properties;
            if (p == null) return -1;
            for (int k = 0; k < 3; k++)
                if (p.Ids[k] == PropertySegment.OoTrigger || p.Ids[k] == PropertySegment.SfTrigger)
                    for (int j = 0; j < 2; j++)
                        if (p.Params[2 * k + j] == slot) return 2 * k + j;
            return -1;
        }

        /// <summary>
        /// Points a map change somewhere else. The zone gets a new script slot (68 of the shipped change-map scripts
        /// serve several zones, which must not move with it): set link, change map, end.
        /// </summary>
        public void RetargetLink(MapLink link, int destinationMap, int destinationLink, int markerType)
        {
            if (Scripts == null) throw new InvalidOperationException("No TRIGGERS.FLX");
            var zone = link.Zone;
            int param = TriggerParam(zone, link.TriggerSlot);
            if (param < 0) throw new InvalidOperationException("The zone's properties no longer name that trigger");
            int flags = Scripts[link.TriggerSlot].Where(o => o.Command == TriggerOp.ChangeMap).Select(o => o.B >> 8).FirstOrDefault();
            Apply(zone.Map, "retarget link", () =>
            {
                int slot = Scripts.FreeSlot();
                SetScript(slot, TriggerScripts.ChangeMapScript(destinationLink, markerType, destinationMap, flags));
                zone.Map.SetPropertyParam(zone, param, slot);
                return 0;
            });
        }

        /// <summary>
        /// A new map change at a position: a copy of a zone (record and property segment, which hold the zone's size
        /// and type; from any map) with its own new script to the destination.
        /// </summary>
        public SceneObject NewLink(byte[] zoneRecord, byte[] zoneSegment, int mapX, int mapY, int z, int destinationMap, int destinationLink, int markerType = 1134)
        {
            if (Scripts == null) throw new InvalidOperationException("No TRIGGERS.FLX");
            if (Nonfixed == null) throw new InvalidOperationException("This map has no nonfixed object file");
            var seg = new PropertySegment(zoneSegment);
            int k = Array.FindIndex(seg.Ids, id => id == PropertySegment.OoTrigger || id == PropertySegment.SfTrigger);
            if (k < 0) throw new ArgumentException("The template zone has no trigger property");
            var o = Apply(Nonfixed, "new link", () =>
            {
                var zone = Nonfixed.Add(zoneRecord, zoneSegment, mapX, mapY, Math.Clamp(z, 0, 0xFFFF));
                Nonfixed.SetLink(zone, 0);
                int slot = Scripts.FreeSlot();
                SetScript(slot, TriggerScripts.ChangeMapScript(destinationLink, markerType, destinationMap));
                Nonfixed.SetPropertyParam(zone, 2 * k, slot);
                Nonfixed.SetPropertyParam(zone, 2 * k + 1, 0);
                return zone;
            });
            return Objects.Of(o);
        }

        /// <summary>A new arrival marker (type 1134 unless told otherwise) with a link, where map changes can land.</summary>
        public SceneObject NewArrival(int link, int mapX, int mapY, int z, int markerType = 1134)
        {
            if (Nonfixed == null) throw new InvalidOperationException("This map has no nonfixed object file");
            var record = TemplateFor(ObjectFileKind.Nonfixed, markerType);
            var o = Apply(Nonfixed, "new arrival", () =>
            {
                var m = Nonfixed.Add(record, null, mapX, mapY, Math.Clamp(z, 0, 0xFFFF));
                Nonfixed.SetLink(m, link);
                return m;
            });
            return Objects.Of(o);
        }

        /// <summary>
        /// Writes the changed files. The first time a file is overwritten, the original is kept beside it as
        /// name.orig. Returns the paths written.
        /// </summary>
        public List<string> Save(string fixedPath, string nonfixedPath)
        {
            var written = new List<string>();
            if (ScriptsChanged && Scripts != null)
            {
                string backup = Scripts.Path + ".orig";
                if (File.Exists(Scripts.Path) && !File.Exists(backup)) File.Copy(Scripts.Path, backup);
                Scripts.Save();
                written.Add(Scripts.Path);
                scriptSaved = scriptVersion;
            }
            if (FixedChanged && Fixed != null && fixedPath != null) { Write(Fixed, fixedPath); written.Add(fixedPath); fixedSaved = fixedVersion; }
            if (NonfixedChanged && Nonfixed != null && nonfixedPath != null) { Write(Nonfixed, nonfixedPath); written.Add(nonfixedPath); nonfixedSaved = nonfixedVersion; }
            if (TerrainChanged && Terrain != null && TerrainPath != null) { Terrain.ShareCopiedChunks(); Write(Terrain.ToBytes(), TerrainPath); written.Add(TerrainPath); terrainSaved = terrainVersion; }
            return written;
        }

        static void Write(ObjectMap m, string path) => Write(m.Data, path);

        static void Write(byte[] data, string path)
        {
            string backup = path + ".orig";
            if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
            string temp = path + ".new";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, true);
        }
    }
}
