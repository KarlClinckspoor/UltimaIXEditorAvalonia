using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using U9.Formats.Flx;

namespace U9.Formats.Text
{
    /// <summary>
    /// A table of lines: static/text.flx (conversation lines, 7,656) or static/misctext.flx. Each record is one
    /// NUL-terminated UTF-16LE string. Records 0, 1 and others like them mark where a conversation's source file began
    /// ("\u9\Source\Usecode\ConvoLib\...\Name.cpp : BEGIN FILE").
    /// </summary>
    public sealed class TextTable
    {
        readonly FlxArchive flx;
        readonly string[] lines;
        public string Path { get; }

        TextTable(FlxArchive archive, string path)
        {
            flx = archive;
            Path = path;
            lines = new string[archive.Count];
            for (int i = 0; i < lines.Length; i++) lines[i] = archive.Has(i) ? Decode(archive.Read(i)) : null;
        }

        public static TextTable Load(string path)
        {
            var ms = new MemoryStream(File.ReadAllBytes(path));
            return new TextTable(FlxArchive.Open(ms), path);
        }

        public static TextTable Read(byte[] bytes) => new TextTable(FlxArchive.Open(new MemoryStream(bytes)), null);

        public static string Decode(byte[] record)
        {
            if (record == null) return null;
            int n = record.Length & ~1;
            string s = Encoding.Unicode.GetString(record, 0, n);
            int z = s.IndexOf('\0');
            return z < 0 ? s : s[..z];
        }

        public static byte[] Encode(string line)
        {
            var b = new byte[(line.Length + 1) * 2];
            Encoding.Unicode.GetBytes(line, 0, line.Length, b, 0);
            return b;
        }

        public int Count => lines.Length;
        public string this[int i] => i >= 0 && i < lines.Length ? lines[i] : null;
        public IReadOnlyList<string> Lines => lines;

        /// <summary>A source-file marker ("... .cpp : BEGIN FILE"), not a line anyone says.</summary>
        public static bool IsMarker(string line) => line != null && line.EndsWith(" : BEGIN FILE", StringComparison.Ordinal);

        /// <summary>The conversation file a marker names ("Exferlem" for "...\Ambrosia\Exferlem.cpp : BEGIN FILE").</summary>
        public static string MarkerName(string line)
        {
            if (!IsMarker(line)) return null;
            string path = line[..line.IndexOf(" : BEGIN FILE", StringComparison.Ordinal)];
            int slash = path.LastIndexOf('\\');
            string file = slash >= 0 ? path[(slash + 1)..] : path;
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        public bool Changed { get; private set; }

        public void Set(int i, string line)
        {
            if (i < 0 || i >= lines.Length) throw new ArgumentOutOfRangeException(nameof(i));
            if (lines[i] == line) return;
            lines[i] = line;
            flx.Write(i, Encode(line ?? ""));
            Changed = true;
        }

        public byte[] ToBytes()
        {
            var ms = new MemoryStream();
            flx.Save(ms, FlxLayout.Preserve);
            return ms.ToArray();
        }

        public void Save(string path = null)
        {
            path ??= Path ?? throw new InvalidOperationException("Loaded from bytes: give a path");
            string temp = path + ".new";
            File.WriteAllBytes(temp, ToBytes());
            File.Move(temp, path, true);
            Changed = false;
        }
    }
}
