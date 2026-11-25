using System.Collections.Generic;

namespace U9.Formats.Triggers.Language
{
    public enum TokenKind { Name, Number, Time, Punct, Comment, Newline, End, Bad }

    /// <summary>A token of the trigger language, with its place in the text (Line and Column from 1).</summary>
    public sealed class Token
    {
        public TokenKind Kind;
        public string Text;
        public int Offset, Line, Column;
        public int Length => Text.Length;
        public override string ToString() => $"{Kind} '{Text}' {Line}:{Column}";
    }

    /// <summary>
    /// Splits trigger-language text into tokens. Statements end at a newline or a ';', so newlines are tokens.
    /// Numbers are decimal or 0x hex, optionally with a fraction and a unit (s, pt, u); 18:30 is a time.
    /// </summary>
    public static class TriggerLexer
    {
        static readonly string[] TwoChar = { "==", "!=", "<=", ">=", "+=", "-=" };

        public static List<Token> Scan(string text, bool keepComments = false)
        {
            var list = new List<Token>();
            text ??= "";
            int i = 0, line = 1, lineStart = 0;
            void Add(TokenKind k, int start, int end) =>
                list.Add(new Token { Kind = k, Text = text.Substring(start, end - start), Offset = start, Line = line, Column = start - lineStart + 1 });
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\n') { Add(TokenKind.Newline, i, i + 1); i++; line++; lineStart = i; continue; }
                if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    int s = i;
                    while (i < text.Length && text[i] != '\n') i++;
                    int e = i;
                    while (e > s && text[e - 1] == '\r') e--;
                    if (keepComments) Add(TokenKind.Comment, s, e);
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    Add(TokenKind.Name, s, i);
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int s = i;
                    if (c == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                    {
                        i += 2;
                        while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
                        Add(TokenKind.Number, s, i);
                        continue;
                    }
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                    if (i + 2 < text.Length && text[i] == ':' && char.IsDigit(text[i + 1]) && char.IsDigit(text[i + 2]))
                    {
                        i += 3;
                        Add(TokenKind.Time, s, i);
                        continue;
                    }
                    if (i + 1 < text.Length && text[i] == '.' && char.IsDigit(text[i + 1]))
                    {
                        i++;
                        while (i < text.Length && char.IsDigit(text[i])) i++;
                    }
                    while (i < text.Length && char.IsLetter(text[i])) i++;      // unit
                    Add(TokenKind.Number, s, i);
                    continue;
                }
                if (i + 1 < text.Length)
                {
                    string two = text.Substring(i, 2);
                    if (System.Array.IndexOf(TwoChar, two) >= 0) { Add(TokenKind.Punct, i, i + 2); i += 2; continue; }
                }
                if ("()[],.:;=<>+-|#!".IndexOf(c) >= 0) { Add(TokenKind.Punct, i, i + 1); i++; continue; }
                Add(TokenKind.Bad, i, i + 1);
                i++;
            }
            list.Add(new Token { Kind = TokenKind.End, Text = "", Offset = text.Length, Line = line, Column = text.Length - lineStart + 1 });
            return list;
        }

        static class Uri
        {
            public static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
