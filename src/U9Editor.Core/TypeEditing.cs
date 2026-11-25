using System;
using System.Collections.Generic;
using U9.Formats.Models;

namespace U9Editor.Core
{
    /// <summary>Type-wide values (TYPES.DAT): every object of a type, on every map, gets them.</summary>
    public sealed partial class MapEditor
    {
        /// <summary>A type's entry changed (its number), by an edit, undo or redo.</summary>
        public event Action<int> TypeEdited;

        long typesSavedAt;
        long TypesTop => TopTouching(s => s.Types != null);
        public bool TypesChanged => TypesTop != typesSavedAt;

        /// <summary>Sets one field of a type's TYPES.DAT entry; one undo step (saved with the maps, the first time keeping .orig).</summary>
        public void SetTypeField(int type, TypeField field, int value)
        {
            if (Types == null) throw new InvalidOperationException("The game has no TYPES.DAT.");
            if ((uint)type >= (uint)Types.Count) throw new ArgumentOutOfRangeException(nameof(type), $"There is no type {type}.");
            if ((uint)value > (uint)TypeTable.MaxOf(field)) throw new ArgumentOutOfRangeException(nameof(value), $"{field} is 0 to {TypeTable.MaxOf(field)}.");
            int before = Types.Get(type, field);
            if (before == value) return;
            undo.Add(new Step { Description = $"{field} of type {type}", Types = new() { (type, field, before, value) } });
            redo.Clear();
            PutType(type, field, value);
        }

        /// <summary>Sets several fields of a type's entry as one undo step (fields already at their value are left out).</summary>
        public void SetTypeFields(int type, IEnumerable<(TypeField Field, int Value)> values)
        {
            if (Types == null) throw new InvalidOperationException("The game has no TYPES.DAT.");
            if ((uint)type >= (uint)Types.Count) throw new ArgumentOutOfRangeException(nameof(type), $"There is no type {type}.");
            var list = new List<(int, TypeField, int, int)>();
            foreach (var (f, v) in values)
            {
                if ((uint)v > (uint)TypeTable.MaxOf(f)) throw new ArgumentOutOfRangeException(nameof(values), $"{f} is 0 to {TypeTable.MaxOf(f)}.");
                int before = Types.Get(type, f);
                if (before != v) list.Add((type, f, before, v));
            }
            if (list.Count == 0) return;
            undo.Add(new Step { Description = list.Count == 1 ? $"{list[0].Item2} of type {type}" : $"type {type}", Types = list });
            redo.Clear();
            foreach (var (t, f, _, v) in list) PutType(t, f, v);
        }

        void PutType(int type, TypeField field, int value)
        {
            Types.Set(type, field, value);
            TypeEdited?.Invoke(type);
        }
    }
}
