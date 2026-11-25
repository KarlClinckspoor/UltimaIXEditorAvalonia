using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using U9.Formats.Text;

namespace U9Editor.Core
{
    /// <summary>What the game will make of a line: the bytes it draws, how it wraps, and what to warn about.</summary>
    public sealed class LineCheck
    {
        public byte[] Bytes = Array.Empty<byte>();
        public List<string> Wrapped = new();
        public int Pages => Math.Max(1, (Wrapped.Count + DialogueCheck.LinesPerPage - 1) / DialogueCheck.LinesPerPage);
        public List<string> Errors = new();      // the game would misbehave
        public List<string> Warnings = new();    // shown differently from what was typed
        public List<string> Notes = new();       // timing, pages
    }

    /// <summary>
    /// The game's rules for a conversation line (docs/exe-research.md, "Dialogue"): it converts the UTF-16 line to the
    /// Windows code page (1252 on Western systems; others fall back to "?"), draws each byte with glyph byte − 0x20 of
    /// font 0x263, wraps at 600 px (a glyph fits while x + advance &lt; 600) in pages of 13 lines, shows an unvoiced
    /// line 10 s per page and a voiced one until its sound ends.
    /// </summary>
    public static class DialogueCheck
    {
        public const int BoxWidth = 600, LinesPerPage = 13, SecondsPerPage = 10;
        static readonly Encoding Cp1252;

        static DialogueCheck()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Cp1252 = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        }

        /// <summary>The bytes the game draws on a Western (code page 1252) Windows.</summary>
        public static byte[] Convert(string line) => Cp1252.GetBytes(line ?? "");

        /// <summary>U+0080-U+009F (C1 controls, which turn into "?") replaced by the characters code page 1252 has at those bytes (U+0092 → ’).</summary>
        public static string FixC1(string line)
        {
            var sb = new StringBuilder(line.Length);
            foreach (char c in line)
            {
                if (c >= 0x80 && c <= 0x9F)
                {
                    string d = Cp1252.GetString(new[] { (byte)c });
                    sb.Append(d.Length == 1 && d[0] != '�' && d[0] != c ? d[0] : c);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static bool BreakAfter(byte b) => b == '!' || b == '.' || b == ';' || b == '?' || b == ',' || b == '-' || b == ':';

        /// <summary>Wraps converted bytes as the layout does: at 600 px, at a space or tab, or after ! . ; ? , - :</summary>
        public static List<string> Wrap(byte[] bytes, GameFont font)
        {
            var lines = new List<string>();
            int start = 0;
            while (start < bytes.Length)
            {
                while (start < bytes.Length && bytes[start] == ' ') start++;      // a wrapped line starts at its first word
                if (start >= bytes.Length) break;
                int x = 0, i = start, lastBreak = -1;
                while (i < bytes.Length)
                {
                    int adv = bytes[i] == 9 ? 4 * font.Advance((byte)' ') : font.Advance(bytes[i]);
                    if (x + adv >= BoxWidth) break;
                    x += adv;
                    if (bytes[i] == ' ' || bytes[i] == 9) lastBreak = i;          // break before the space
                    else if (BreakAfter(bytes[i])) lastBreak = i + 1;
                    i++;
                }
                int end = i >= bytes.Length ? bytes.Length : lastBreak > start ? lastBreak : Math.Max(i, start + 1);
                lines.Add(Cp1252.GetString(bytes, start, end - start).TrimEnd());
                start = end;
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        /// <param name="voiceSeconds">The clip's length, or null for a line without voice.</param>
        /// <param name="sameConversation">The other lines of its conversation (for replies that would read the same).</param>
        public static LineCheck Check(string line, GameFont font, double? voiceSeconds, IEnumerable<(int Line, string Text)> sameConversation = null)
        {
            var r = new LineCheck();
            line ??= "";
            r.Bytes = Convert(line);
            if (line.Length == 0) r.Errors.Add("An empty line shows the exe's English text instead (as if the line were missing), with no voice.");

            var controls = line.Where(c => c < 0x20).Distinct().ToList();
            if (controls.Contains('\u0005')) r.Errors.Add("Control code 0x05 ends the line without being consumed by the layout: never use it.");
            var others = controls.Where(c => c != '\u0005').ToList();
            if (others.Count > 0)
                r.Warnings.Add("Control codes " + string.Join(", ", others.Select(c => $"0x{(int)c:X2}")) +
                               " change the layout (0x0A, 0x0B, 0x0D break; 0x07 colour; 0x0C font; 0x09 tab); no shipped line uses any.");

            var c1 = line.Where(c => c >= 0x80 && c <= 0x9F).Distinct().ToList();
            if (c1.Count > 0) r.Warnings.Add("Characters U+0080-U+009F (" + string.Join(" ", c1.Select(c => $"U+{(int)c:X4}")) + ") show as \"?\": use Fix characters (U+0092 → ’).");
            var lost = new List<string>();
            foreach (char c in line.Distinct())
            {
                if (c < 0x20 || c >= 0x80 && c <= 0x9F) continue;
                var b = Cp1252.GetBytes(c.ToString());
                string back = Cp1252.GetString(b);
                if (back != c.ToString()) lost.Add($"{c} (U+{(int)c:X4}) → {back}");
            }
            if (lost.Count > 0) r.Warnings.Add("Not in the game's code page, shown as: " + string.Join(", ", lost.Take(10)) + (lost.Count > 10 ? " …" : "") +
                                               ". The game draws single bytes with a 224-glyph font: CJK is impossible without an exe patch.");
            var blank = r.Bytes.Where(font.Blank).Distinct().Select(b => Cp1252.GetString(new[] { b })).ToList();
            if (blank.Count > 0) r.Warnings.Add("The subtitle font has no glyph for " + string.Join(" ", blank) + ": drawn as a gap.");

            r.Wrapped = Wrap(r.Bytes, font);
            int lines = r.Wrapped.Count;
            if (voiceSeconds is double s)
            {
                r.Notes.Add($"{lines} line{(lines == 1 ? "" : "s")} on screen; shown while its {s:0.0} s voice plays.");
                if (lines > LinesPerPage) r.Errors.Add($"Wraps to {lines} lines, more than a page ({LinesPerPage}): a voiced line closes when its sound ends, so the lines after {LinesPerPage} are never shown.");
            }
            else
            {
                r.Notes.Add($"{lines} line{(lines == 1 ? "" : "s")}, {r.Pages} page{(r.Pages == 1 ? "" : "s")}; no voice: shown {SecondsPerPage} s per page (or until a click / Enter).");
            }

            if (sameConversation != null)
            {
                string mine = Encoding.Latin1.GetString(r.Bytes);
                var twins = sameConversation.Where(o => o.Text != null && Encoding.Latin1.GetString(Convert(o.Text)) == mine).Select(o => o.Line).ToList();
                if (twins.Count > 0 && line.Length > 0)
                    r.Warnings.Add($"Reads the same as line{(twins.Count == 1 ? "" : "s")} {string.Join(", ", twins)} of this conversation: if they are replies in one menu, the game takes the first.");
            }
            return r;
        }
    }
}
