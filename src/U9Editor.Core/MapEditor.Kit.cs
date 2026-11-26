using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    public sealed partial class MapEditor
    {
        /// <summary>
        /// Places kit pieces (one undo step): in the fixed file unless <paramref name="kind"/> says otherwise, each a
        /// copy of one of its type on this map or on <paramref name="source"/> (the kit's map). Returns the new objects.
        /// </summary>
        public List<SceneObject> PlaceKit(IReadOnlyList<KitPlacement> plan, ObjectMap source, ObjectFileKind kind = ObjectFileKind.Fixed, string description = null)
        {
            if (plan.Count == 0) return new List<SceneObject>();
            var map = kind == ObjectFileKind.Fixed ? Fixed : Nonfixed;
            if (map == null) throw new InvalidOperationException($"This map has no {(kind == ObjectFileKind.Fixed ? "fixed" : "nonfixed")} object file");
            var made = Apply(map, description ?? (plan.Count == 1 ? "place piece" : $"build {plan.Count} pieces"),
                             () => KitBuilding.AddAll(map, source, plan));
            return made.Select(o => Objects.Of(o)).Where(o => o != null).ToList();
        }

        /// <summary>Clears the redo list (a temporary edit, such as a ghost piece, was just undone and must not come back).</summary>
        public void ForgetRedo() => redo.Clear();
    }
}
