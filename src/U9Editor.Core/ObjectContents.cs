using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Books;
using U9.Formats.Objects;

namespace U9Editor.Core
{
    /// <summary>What objects carry: their properties, what is inside them, and book texts.</summary>
    public sealed partial class MapEditor
    {
        /// <summary>The game's book texts (shared by every map's editor); null when the game has none.</summary>
        public BookTexts Books { get; set; }

        /// <summary>A book's text changed (its number), by an edit, undo or redo.</summary>
        public event Action<int> BookEdited;

        long booksSavedAt;
        long BooksTop => TopTouching(s => s.Book != null);
        public bool BooksChanged => BooksTop != booksSavedAt;

        /// <summary>The properties of an object (or of an object inside one), following chained segments.</summary>
        public List<ObjectProperty> PropertiesOf(MapObject o) => o?.Map?.PropertiesOf(o) ?? new List<ObjectProperty>();

        /// <summary>What is inside a container or carried by an NPC (records in no list).</summary>
        public List<MapObject> ContentsOf(MapObject o) => o?.Map?.ContentsOf(o) ?? new List<MapObject>();

        /// <summary>
        /// Sets a property's two values on <paramref name="target"/>, which is <paramref name="owner"/>'s own record or
        /// one inside it; one undo step.
        /// </summary>
        public void SetProperty(SceneObject owner, MapObject target, ObjectProperty p, int a, int b)
        {
            if (owner == null || target == null || p == null) throw new ArgumentNullException(nameof(p));
            if ((uint)a > 0xFFFF || (uint)b > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(a), "Property values are 0 to 65535.");
            var map = owner.Source.Map;
            Apply(map, $"set {p.Name}", () =>
            {
                map.SetProperty(target, p, a, b);
                return 0;
            });
        }

        /// <summary>Replaces a book's title and text; one undo step (saved into the books file with the maps).</summary>
        public void SetBook(int number, string title, string text)
        {
            if (Books == null) throw new InvalidOperationException("The game has no book texts.");
            var before = Books.Get(number);
            if (before is { } bv && bv.Title == title && bv.Text == text) return;
            undo.Add(new Step { Description = $"text of book {number}", Book = (number, before, (title, text)) });
            redo.Clear();
            PutBook(number, (title, text));
        }

        void PutBook(int number, (string Title, string Text)? value)
        {
            if (Books == null) return;
            if (value is { } v) Books.Set(number, v.Title, v.Text); else Books.Remove(number);
            BookEdited?.Invoke(number);
        }
    }
}
