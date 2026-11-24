using System;
using System.Collections.Generic;
using System.Numerics;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>One object as the view sees it.</summary>
    public sealed class SceneObject
    {
        public int Id { get; init; }
        public ObjectFileKind Kind { get; init; }
        public MapObject Source { get; init; }
        public Vector3 World { get; set; }
        public string Name { get; init; }
        /// <summary>A trigger zone that changes map (see <see cref="MapLinks"/>).</summary>
        public bool IsLinkZone { get; set; }
        /// <summary>An arrival marker (nonfixed type 1134), where map changes land.</summary>
        public bool IsArrival => Kind == ObjectFileKind.Nonfixed && Source.Type == 1134;
        /// <summary>The model drawn for it (set by <see cref="ModelScene"/>), or -1: then it is drawn as a marker.</summary>
        public int Model { get; set; } = -1;
        /// <summary>Deleted from the map as far as the view knows (see <see cref="MapObject.Removed"/>).</summary>
        public bool Hidden { get; set; }
        /// <summary>A sphere round the model in world space (set by <see cref="ModelScene"/>), for picking.</summary>
        public Vector3 BoundCentre { get; set; }
        public float BoundRadius { get; set; }
        public override string ToString() =>
            $"{(Kind == ObjectFileKind.Fixed ? "fixed" : "nonfixed")} #{Id}: type {Source.Type}{(Name != null ? " \"" + Name + "\"" : "")}  " +
            $"at point ({Source.PointX:F2}, {Source.PointRow:F2}) height {Source.TerrainHeight:F1}  flags 0x{Source.Flags:X}" +
            (Kind == ObjectFileKind.Nonfixed ? $"  link {Source.Link}  model {Source.Model}" : "") + (IsLinkZone ? "  [map change]" : "");
    }

    /// <summary>A map's fixed and nonfixed objects in world space, for drawing and picking.</summary>
    public sealed class SceneObjects
    {
        public readonly List<SceneObject> All = new List<SceneObject>();

        readonly Dictionary<MapObject, SceneObject> bySource = new Dictionary<MapObject, SceneObject>();
        TypeNames names;

        public static SceneObjects Build(ObjectMap fixedObjects, ObjectMap nonfixedObjects, MapSpace space, TypeNames names)
        {
            var s = new SceneObjects { names = names };
            foreach (var m in new[] { fixedObjects, nonfixedObjects })
            {
                if (m == null) continue;
                foreach (var o in m.Objects) s.Add(o, space);
            }
            return s;
        }

        /// <summary>Adds an object (a new one, or the first time it is seen); returns it, or the one already there.</summary>
        public SceneObject Add(MapObject o, MapSpace space)
        {
            if (bySource.TryGetValue(o, out var have)) return have;
            var so = new SceneObject { Id = All.Count, Kind = o.Map.Kind, Source = o, Name = names?[o.Type] };
            so.World = WorldOf(o, space);
            All.Add(so);
            bySource[o] = so;
            return so;
        }

        public SceneObject Of(MapObject o) => o != null && bySource.TryGetValue(o, out var so) ? so : null;

        /// <summary>The object's marker position, from its record.</summary>
        public static Vector3 WorldOf(MapObject o, MapSpace space) =>
            new Vector3((float)o.PointX, MapSpace.WorldYFromObject(o.Z), space.WorldZ((float)o.PointRow));

        /// <summary>Recomputes an object's position after an edit.</summary>
        public void Refresh(SceneObject o, MapSpace space) => o.World = WorldOf(o.Source, space);

        /// <summary>
        /// The object whose model a ray meets first, before <paramref name="maxT"/> along it (dir normalised), or null;
        /// <paramref name="hitT"/> is the distance.
        /// </summary>
        public SceneObject PickModel(ModelScene models, MapSpace space, Vector3 origin, Vector3 dir, float maxT,
                                     Func<SceneObject, bool> visible, out float hitT)
        {
            SceneObject best = null;
            hitT = maxT;
            if (models == null) return null;
            foreach (var o in All)
            {
                if (o.Model < 0 || o.Source.Removed || (visible != null && !visible(o))) continue;
                // the sphere first: the ray's nearest approach to its centre
                var oc = o.BoundCentre - origin;
                float along = Vector3.Dot(oc, dir);
                if (along + o.BoundRadius < 0 || along - o.BoundRadius > hitT) continue;
                if ((oc - dir * along).LengthSquared() > o.BoundRadius * o.BoundRadius) continue;
                float t = models.RayHit(o, ModelScene.Placement(o.Source, space), origin, dir);
                if (t < hitT) { hitT = t; best = o; }
            }
            return best;
        }

        /// <summary>
        /// The object drawn nearest to a screen point, within <paramref name="radius"/> pixels, that is not hidden
        /// behind <paramref name="maxDepth"/> (the terrain hit's distance from the eye; infinity for none).
        /// </summary>
        public SceneObject Pick(Matrix4x4 viewProj, Vector3 eye, float px, float py, float width, float height,
                                float radius, float maxDepth, Func<SceneObject, bool> visible)
        {
            SceneObject best = null;
            float bestScore = float.MaxValue;
            foreach (var o in All)
            {
                if (o.Source.Removed || (visible != null && !visible(o))) continue;
                var c = Vector4.Transform(new Vector4(o.World, 1), viewProj);
                if (c.W <= 0.01f) continue;
                float sx = (c.X / c.W * 0.5f + 0.5f) * width, sy = (0.5f - c.Y / c.W * 0.5f) * height;
                float d2 = (sx - px) * (sx - px) + (sy - py) * (sy - py);
                if (d2 > radius * radius) continue;
                float dist = Vector3.Distance(eye, o.World);
                if (dist > maxDepth + 0.75f) continue;
                float score = d2 + dist * 0.01f;
                if (score < bestScore) { bestScore = score; best = o; }
            }
            return best;
        }
    }
}
