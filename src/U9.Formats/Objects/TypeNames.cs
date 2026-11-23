using System.Collections.Generic;
using System.Text;
using U9.Formats.Flx;

namespace U9.Formats.Objects
{
    /// <summary>
    /// static/TYPENAME.FLX: one record per object type, a 6-byte prefix (u32, u16; meaning unknown) and a
    /// NUL-terminated Latin-1 name. Most scenery types have an empty name.
    /// </summary>
    public sealed class TypeNames
    {
        readonly Dictionary<int, string> names = new Dictionary<int, string>();

        public static TypeNames Load(string path)
        {
            var t = new TypeNames();
            using var a = FlxArchive.Open(path);
            foreach (int i in a.UsedIndices())
            {
                var r = a.Read(i);
                if (r.Length <= 6) continue;
                int end = System.Array.IndexOf(r, (byte)0, 6);
                string s = Encoding.Latin1.GetString(r, 6, (end < 0 ? r.Length : end) - 6);
                if (s.Length > 0) t.names[i] = s;
            }
            return t;
        }

        /// <summary>The type's name, or null when it has none.</summary>
        public string this[int type] => names.TryGetValue(type, out var s) ? s : null;
        public int Count => names.Count;
    }
}
