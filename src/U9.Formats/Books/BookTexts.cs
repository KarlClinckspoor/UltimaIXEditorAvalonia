using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using U9.Formats.Flx;

namespace U9.Formats.Books
{
    /// <summary>
    /// static/BOOKS-EN.FLX (one per language): the texts of books, plaques, signs and grave markers. Record n - 1 is
    /// book number n (the Book Number property, 0x4D, names it; each title starts "n: "). A record is a u32 length and
    /// the NUL-terminated title, then a u32 length and the text (CR LF lines; `f1, `f2 ... pick the font). Texts are
    /// kept as Latin-1, so every byte comes back as it was; a few records hold leftovers (one an embedded document).
    /// </summary>
    public sealed class BookTexts
    {
        static readonly Encoding Latin1 = Encoding.Latin1;
        readonly Dictionary<int, byte[]> records = new Dictionary<int, byte[]>();
        readonly HashSet<int> changed = new HashSet<int>();
        int slots;

        public string Path { get; private set; }
        public bool HasChanges => changed.Count > 0;

        public static BookTexts Load(string path)
        {
            var b = new BookTexts { Path = path };
            using var a = FlxArchive.Open(path);
            b.slots = a.Count;
            foreach (int i in a.UsedIndices()) b.records[i] = a.Read(i);
            return b;
        }

        /// <summary>The book files of a game's static folder (BOOKS-EN.FLX and any other languages).</summary>
        public static IEnumerable<string> FilesIn(string staticFolder) =>
            Directory.Exists(staticFolder)
                ? Directory.EnumerateFiles(staticFolder, "books-*.flx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                : Array.Empty<string>();

        /// <summary>The book numbers that have a record.</summary>
        public IEnumerable<int> Numbers { get { foreach (int i in records.Keys) yield return i + 1; } }

        public bool Has(int number) => records.ContainsKey(number - 1);

        /// <summary>A book's title and text, or null when there is no such record (or it cannot be read as one).</summary>
        public (string Title, string Text)? Get(int number)
        {
            if (!records.TryGetValue(number - 1, out var r)) return null;
            return Decode(r);
        }

        /// <summary>Replaces a book's title and text (saved with <see cref="Save"/>).</summary>
        public void Set(int number, string title, string text)
        {
            if (number < 1 || number > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(number));
            records[number - 1] = Encode(title, text);
            changed.Add(number - 1);
            if (number > slots) slots = number;
        }

        /// <summary>Takes a book's record away (saved with <see cref="Save"/>).</summary>
        public void Remove(int number)
        {
            if (records.Remove(number - 1)) changed.Add(number - 1);
        }

        /// <summary>The first number after the highest one used, for a new book.</summary>
        public int FreeNumber()
        {
            int max = 0;
            foreach (int i in records.Keys) max = Math.Max(max, i + 1);
            return max + 1;
        }

        public static (string Title, string Text)? Decode(byte[] r)
        {
            if (r.Length < 8) return null;
            int tl = BinaryPrimitives.ReadInt32LittleEndian(r);
            if (tl < 0 || 4 + tl + 4 > r.Length) return null;
            int xl = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(4 + tl));
            if (xl < 0 || 8 + tl + xl > r.Length) return null;
            string title = Latin1.GetString(r, 4, tl > 0 && r[4 + tl - 1] == 0 ? tl - 1 : tl);
            return (title, Latin1.GetString(r, 8 + tl, xl));
        }

        public static byte[] Encode(string title, string text)
        {
            byte[] t = Latin1.GetBytes(title ?? ""), x = Latin1.GetBytes(text ?? "");
            var r = new byte[8 + t.Length + 1 + x.Length];
            BinaryPrimitives.WriteInt32LittleEndian(r, t.Length + 1);
            t.CopyTo(r, 4);
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4 + t.Length + 1), x.Length);
            x.CopyTo(r, 8 + t.Length + 1);
            return r;
        }

        /// <summary>Writes the changed records into the archive at <paramref name="path"/> (default: the file read), keeping the rest.</summary>
        public void Save(string path = null)
        {
            path ??= Path;
            using (var a = FlxArchive.Open(Path))
            {
                if (slots > a.Count) a.Resize(slots);
                foreach (int i in changed) a.Write(i, records.TryGetValue(i, out var r) ? r : null);
                a.Save(path);
            }
            changed.Clear();
            Path = path;
        }
    }
}
