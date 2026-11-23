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
        public override string ToString() =>
            $"{(Kind == ObjectFileKind.Fixed ? "fixed" : "nonfixed")} #{Id}: type {Source.Type}{(Name != null ? " \"" + Name + "\"" : "")}  " +
            $"at point ({Source.PointX:F2}, {Source.PointRow:F2}) height {Source.TerrainHeight:F1}  flags 0x{Source.Flags:X}" +
            (Kind == ObjectFileKind.Nonfixed ? $"  link {Source.Link}  model {Source.Model}" : "") + (IsLinkZone ? "  [map change]" : "");
    }

    /// <summary>A map's fixed and nonfixed objects in world space, for drawing and picking.</summary>
    public sealed class SceneObjects
    {
        public readonly List<SceneObject> All = new List<SceneObject>();

        public static SceneObjects Build(ObjectMap fixedObjects, ObjectMap nonfixedObjects, MapSpace space, TypeNames names)
        {
            var s = new SceneObjects();
            foreach (var m in new[] { fixedObjects, nonfixedObjects })
            {
                if (m == null) continue;
                foreach (var o in m.Objects)
                    s.All.Add(new SceneObject
                    {
                        Id = s.All.Count,
                        Kind = m.Kind,
                        Source = o,
                        World = new Vector3((float)o.PointX, MapSpace.WorldYFromObject(o.Z), space.WorldZ((float)o.PointRow)),
                        Name = names?[o.Type],
                    });
            }
            return s;
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
                if (visible != null && !visible(o)) continue;
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
