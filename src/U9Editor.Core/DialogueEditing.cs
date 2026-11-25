using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Text;
using U9.Mods;

namespace U9Editor.Core
{
    /// <summary>One conversation file of text.flx: the lines from its "BEGIN FILE" marker to the next.</summary>
    public sealed class Conversation
    {
        public string Name;          // "Exferlem"
        public string Source;        // the marker's path
        public int First, Last;      // line records, marker excluded
        public override string ToString() => $"{Name}  ({Last - First + 1})";
    }

    /// <summary>
    /// The game's conversation lines (static/text.flx) with their voice clips (sound/Speech.flx: the record of the
    /// same number). Text edits and voice replacements are undo steps; saving keeps a .orig of each file the first time.
    /// </summary>
    public sealed class DialogueEditing
    {
        public TextTable Text { get; }
        public string TextPath { get; }
        public string SpeechPath { get; }
        public IReadOnlyList<Conversation> Conversations { get; }
        /// <summary>The subtitle font (record 0x263 of static/bitmap16.flx), or null when the file is missing.</summary>
        public GameFont Font { get; }
        readonly Dictionary<int, SpeechClip> clips;

        public event Action<int> LineChanged;

        DialogueEditing(TextTable text, string textPath, string speechPath, Dictionary<int, SpeechClip> clips, GameFont font)
        {
            Text = text; TextPath = textPath; SpeechPath = speechPath; this.clips = clips; Font = font;
            Conversations = Split(text);
        }

        public static DialogueEditing Open(string root)
        {
            string text = GamePaths.Find(root, "static/text.flx") ?? throw new FileNotFoundException("No static/text.flx in " + root);
            string speech = GamePaths.Find(root, "sound/speech.flx");
            var clips = speech != null ? SpeechTable.ReadHeaders(speech).ToDictionary(c => c.Record) : new Dictionary<int, SpeechClip>();
            GameFont font = null;
            foreach (var f in new[] { "static/bitmap16.flx", "static/bitmapsh.flx", "static/bitmapc.flx" })
                if (font == null && GamePaths.Find(root, f) is string p)
                {
                    try { font = GameFont.Load(p); } catch (InvalidDataException) { }
                }
            var ed = new DialogueEditing(TextTable.Load(text), text, speech, clips, font);
            if (GamePaths.Find(root, "runtime/npc.flx") is string npc)
            {
                try { ed.NpcNames = VowelPlay.NpcNames(U9.Formats.Combat.NpcTable.Load(npc)).ToList(); } catch (InvalidDataException) { }
            }
            return ed;
        }

        static List<Conversation> Split(TextTable t)
        {
            var list = new List<Conversation>();
            Conversation cur = null;
            for (int i = 0; i < t.Count; i++)
            {
                string s = t[i];
                if (TextTable.IsMarker(s))
                {
                    if (cur != null) { cur.Last = i - 1; list.Add(cur); }
                    cur = new Conversation { Name = TextTable.MarkerName(s), Source = s[..s.IndexOf(" : BEGIN FILE", StringComparison.Ordinal)], First = i + 1 };
                }
            }
            if (cur != null) { cur.Last = t.Count - 1; list.Add(cur); }
            if (list.Count > 0 && list[0].First > 1 && !TextTable.IsMarker(t[0]))
                list.Insert(0, new Conversation { Name = "(before the first file)", Source = "", First = 0, Last = list[0].First - 2 });
            return list;
        }

        /// <summary>The conversation a line belongs to.</summary>
        public Conversation ConversationOf(int line) => Conversations.FirstOrDefault(c => line >= c.First && line <= c.Last);

        /// <summary>The other lines of a line's conversation (for the same-text check).</summary>
        public IEnumerable<(int Line, string Text)> Neighbours(int line)
        {
            var c = ConversationOf(line);
            if (c == null) yield break;
            for (int i = c.First; i <= c.Last; i++) if (i != line) yield return (i, Text[i]);
        }

        /// <summary>What the game makes of a line's current text (or of another text for it).</summary>
        public LineCheck Check(int line, string text = null) =>
            Font == null ? null : DialogueCheck.Check(text ?? Text[line], Font, SecondsOf(line), Neighbours(line));

        // ---- voice clips ----

        readonly Dictionary<int, byte[]> newClips = new();     // records replaced in this session
        readonly Dictionary<int, int> sampleCounts = new();

        public SpeechClip? ClipOf(int line)
        {
            if (newClips.TryGetValue(line, out var r)) return SpeechClip.Parse(line, r, r.Length);
            return clips.TryGetValue(line, out var c) ? c : null;
        }

        public int ClipCount => clips.Count;
        public bool ClipReplaced(int line) => newClips.ContainsKey(line);

        /// <summary>The clip's length in seconds (codec 2: the sample count its data starts with; codec 0: its PCM size), else null.</summary>
        public double? SecondsOf(int line)
        {
            if (ClipOf(line) is not SpeechClip c || c.Rate <= 0) return null;
            if (c.Codec == 0 && c.Bits > 0 && c.Channels > 0) return c.DataLength / (double)(c.Rate * c.Bits / 8 * c.Channels);
            if (c.Codec != 2) return null;
            if (newClips.TryGetValue(line, out var r)) return MicroTalk.SampleCount(r.AsSpan(SpeechClip.HeaderSize)) / (double)c.Rate;
            return sampleCounts.TryGetValue(line, out int n) ? n / (double)c.Rate : null;
        }

        byte[] Record(int line) => newClips.TryGetValue(line, out var r) ? r : SpeechPath == null ? null : SpeechTable.ReadRecord(SpeechPath, line);

        /// <summary>A line's voice clip as a WAV file's bytes (MicroTalk decoded, PCM as is), or null when it has none.</summary>
        public byte[] ClipWav(int line)
        {
            if (ClipOf(line) is not SpeechClip c) return null;
            var rec = Record(line);
            if (rec == null) return null;
            if (c.Codec == 2) return MicroTalk.Wav(MicroTalk.Decode(rec, SpeechClip.HeaderSize), c.Rate);
            if (c.Codec == 0 && c.Bits == 16)
            {
                var samples = new short[c.DataLength / 2];
                Buffer.BlockCopy(rec, SpeechClip.HeaderSize, samples, 0, samples.Length * 2);
                return MicroTalk.Wav(samples, c.Rate, c.Channels);
            }
            return null;
        }

        /// <summary>Reads the sample count at the start of every MicroTalk clip; 7,000 seeks, so on demand.</summary>
        public void ReadClipLengths()
        {
            if (SpeechPath == null || sampleCounts.Count > 0) return;
            using var f = File.OpenRead(SpeechPath);
            var head = new byte[0x80];
            f.ReadExactly(head);
            int count = BitConverter.ToInt32(head, 0x50);
            var dir = new byte[8 * count];
            f.ReadExactly(dir);
            var w = new byte[4];
            foreach (var c in clips.Values)
            {
                if (c.RecordLength < SpeechClip.HeaderSize + 4 || c.Codec != 2) continue;
                f.Position = BitConverter.ToUInt32(dir, 8 * c.Record) + SpeechClip.HeaderSize;
                f.ReadExactly(w);
                sampleCounts[c.Record] = BitConverter.ToInt32(w, 0);
            }
        }

        public const int SpeechRate = 22050;

        /// <summary>
        /// Replaces a line's voice with a WAV, stored as uncompressed PCM (codec 0), 22,050 Hz 16-bit mono (converted
        /// when the WAV is not: NPC voices play as 3D sound, which needs mono). The game's speech code takes codec 0
        /// (docs/exe-research.md), but no shipped clip uses it: untried in the game. Throws InvalidDataException for a
        /// file it cannot read.
        /// </summary>
        public void ImportWav(int line, byte[] wav)
        {
            if (line < 0 || line >= Text.Count) throw new ArgumentOutOfRangeException(nameof(line));
            var samples = WavReader.ToMono16(wav, SpeechRate);
            string name = ClipOf(line)?.Name is string n && n.Length > 0 ? n : $"Line_{line:D5}.wav";
            var rec = PcmRecord(line, name, samples, SpeechRate);
            bool had = newClips.TryGetValue(line, out var prev);
            newClips[line] = rec;
            Push(new ClipStep(line, had ? prev : null, rec));
        }

        /// <summary>A Speech.flx record holding PCM (codec 0): the 0x3C-byte header, then the samples.</summary>
        public static byte[] PcmRecord(int line, string name, short[] samples, int rate)
        {
            var r = new byte[SpeechClip.HeaderSize + samples.Length * 2];
            BitConverter.TryWriteBytes(r.AsSpan(0), line);
            var nb = System.Text.Encoding.ASCII.GetBytes(name);
            Array.Copy(nb, 0, r, 4, Math.Min(nb.Length, 35));
            BitConverter.TryWriteBytes(r.AsSpan(0x28), samples.Length * 2);
            BitConverter.TryWriteBytes(r.AsSpan(0x2C), rate);
            BitConverter.TryWriteBytes(r.AsSpan(0x30), 16);
            BitConverter.TryWriteBytes(r.AsSpan(0x34), 1);
            BitConverter.TryWriteBytes(r.AsSpan(0x38), 0);       // codec 0: never anything above 2 (the game would call through NULL)
            Buffer.BlockCopy(samples, 0, r, SpeechClip.HeaderSize, samples.Length * 2);
            return r;
        }

        // ---- undo ----

        abstract record Step(int Line);
        sealed record TextStep(int Line, string Before, string After) : Step(Line);
        sealed record ClipStep(int Line, byte[] Before, byte[] After) : Step(Line);   // Before null: the shipped clip
        sealed record BatchStep(int Line, List<(int Line, string Before, string After)> Lines) : Step(Line);

        readonly List<Step> undo = new(), redo = new();
        int savedAt;
        bool clipsDirty;

        public bool HasChanges => undo.Count != savedAt;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;

        void Push(Step s)
        {
            if (savedAt > undo.Count) savedAt = -1;
            if (s is ClipStep) clipsDirty = true;
            undo.Add(s);
            redo.Clear();
            LineChanged?.Invoke(s.Line);
        }

        /// <summary>Several lines changed as one undo step (vowel play). False when nothing changes.</summary>
        public bool SetLines(IEnumerable<(int Line, string Before, string After)> lines)
        {
            var list = lines.Where(l => Text[l.Line] != l.After).Select(l => (l.Line, Text[l.Line], l.After)).ToList();
            if (list.Count == 0) return false;
            foreach (var l in list) Text.Set(l.Line, l.After);
            foreach (var l in list.Skip(1)) LineChanged?.Invoke(l.Line);
            Push(new BatchStep(list[0].Line, list));
            return true;
        }

        /// <summary>The names of runtime/NPC.FLX (empty when the file is missing), for vowel play.</summary>
        public IReadOnlyList<string> NpcNames { get; private set; } = Array.Empty<string>();

        public bool SetLine(int line, string text)
        {
            string before = Text[line];
            if (before == text) return false;
            Text.Set(line, text);
            Push(new TextStep(line, before, text));
            return true;
        }

        void Put(Step s, bool forward)
        {
            switch (s)
            {
                case TextStep t: Text.Set(t.Line, forward ? t.After : t.Before); break;
                case BatchStep b:
                    foreach (var l in b.Lines) Text.Set(l.Line, forward ? l.After : l.Before);
                    foreach (var l in b.Lines) if (l.Line != b.Line) LineChanged?.Invoke(l.Line);
                    break;
                case ClipStep c:
                    var rec = forward ? c.After : c.Before;
                    if (rec != null) newClips[c.Line] = rec; else newClips.Remove(c.Line);
                    clipsDirty = true;
                    break;
            }
        }

        public int Undo()
        {
            if (undo.Count == 0) return -1;
            var s = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            Put(s, false);
            redo.Add(s);
            LineChanged?.Invoke(s.Line);
            return s.Line;
        }

        public int Redo()
        {
            if (redo.Count == 0) return -1;
            var s = redo[^1];
            redo.RemoveAt(redo.Count - 1);
            Put(s, true);
            undo.Add(s);
            LineChanged?.Invoke(s.Line);
            return s.Line;
        }

        /// <summary>Lines whose text contains the query (case-insensitive), or whose clip name or number matches.</summary>
        public IEnumerable<int> Find(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) yield break;
            query = query.Trim();
            bool number = int.TryParse(query, out int n);
            for (int i = 0; i < Text.Count; i++)
            {
                string s = Text[i];
                if (s == null || TextTable.IsMarker(s)) continue;
                if (number && i == n || s.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    ClipOf(i) is SpeechClip c && c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) yield return i;
            }
        }

        /// <summary>
        /// Writes text.flx and, when voices were replaced, sound/Speech.flx (each keeping a .orig the first time; Speech.flx
        /// is rebuilt from its .orig with the replaced clips, so a clip put back to the shipped one is exact). Returns the
        /// paths written.
        /// </summary>
        public List<string> Save()
        {
            var written = new List<string>();
            if (!HasChanges) return written;
            if (Text.Changed)
            {
                string backup = TextPath + ".orig";
                if (File.Exists(TextPath) && !File.Exists(backup)) File.Copy(TextPath, backup);
                Text.Save(TextPath);
                written.Add(TextPath);
            }
            if (clipsDirty && SpeechPath != null)
            {
                string backup = SpeechPath + ".orig";
                if (!File.Exists(backup)) File.Copy(SpeechPath, backup);
                using (var a = U9.Formats.Flx.FlxArchive.Open(new MemoryStream(File.ReadAllBytes(backup))))
                {
                    foreach (var (line, rec) in newClips) a.Write(line, rec);
                    a.Save(SpeechPath + ".new");
                }
                File.Move(SpeechPath + ".new", SpeechPath, true);
                written.Add(SpeechPath);
                clipsDirty = false;
            }
            savedAt = undo.Count;
            return written;
        }
    }

    /// <summary>Reads a RIFF WAV (PCM 8/16/24/32-bit or 32-bit float, any channels and rate) as 16-bit mono at a given rate.</summary>
    public static class WavReader
    {
        public static short[] ToMono16(byte[] wav, int rate)
        {
            if (wav.Length < 12 || System.Text.Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
                throw new InvalidDataException("not a WAV file");
            int fmt = 0, channels = 0, srcRate = 0, bits = 0, dataAt = -1, dataLen = 0;
            for (int p = 12; p + 8 <= wav.Length;)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, p, 4);
                int len = BitConverter.ToInt32(wav, p + 4);
                if (len < 0 || p + 8 + len > wav.Length) len = wav.Length - p - 8;
                if (id == "fmt " && len >= 16)
                {
                    fmt = BitConverter.ToUInt16(wav, p + 8);
                    channels = BitConverter.ToUInt16(wav, p + 10);
                    srcRate = BitConverter.ToInt32(wav, p + 12);
                    bits = BitConverter.ToUInt16(wav, p + 22);
                    if (fmt == 0xFFFE && len >= 40) fmt = BitConverter.ToUInt16(wav, p + 32);   // WAVE_FORMAT_EXTENSIBLE: the sub-format
                }
                else if (id == "data") { dataAt = p + 8; dataLen = len; }
                p += 8 + len + (len & 1);
            }
            if (dataAt < 0 || channels < 1 || srcRate < 1000) throw new InvalidDataException("the WAV has no sound data or no format");
            if (!(fmt == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32) || fmt == 3 && bits == 32))
                throw new InvalidDataException($"WAV format {fmt} with {bits}-bit samples: use uncompressed PCM or float");
            int frame = channels * bits / 8, frames = dataLen / frame;
            var mono = new double[frames];
            for (int i = 0; i < frames; i++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int o = dataAt + i * frame + c * bits / 8;
                    sum += bits switch
                    {
                        8 => (wav[o] - 128) / 128.0,
                        16 => BitConverter.ToInt16(wav, o) / 32768.0,
                        24 => ((wav[o] | wav[o + 1] << 8 | (sbyte)wav[o + 2] << 16)) / 8388608.0,
                        _ => fmt == 3 ? BitConverter.ToSingle(wav, o) : BitConverter.ToInt32(wav, o) / 2147483648.0,
                    };
                }
                mono[i] = sum / channels;
            }
            int outLen = (int)((long)frames * rate / srcRate);
            var r = new short[outLen];
            for (int i = 0; i < outLen; i++)
            {
                double t = (double)i * srcRate / rate;
                int a = (int)t;
                double f = t - a, v = a + 1 < frames ? mono[a] * (1 - f) + mono[a + 1] * f : mono[Math.Min(a, frames - 1)];
                r[i] = (short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue);
            }
            return r;
        }
    }
}
