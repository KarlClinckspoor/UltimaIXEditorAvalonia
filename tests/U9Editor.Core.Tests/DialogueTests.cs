using System;
using System.IO;
using System.Linq;
using U9.Formats.Text;
using U9.Mods;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class DialogueTests
    {
        static string Root()
        {
            string env = Environment.GetEnvironmentVariable("U9_GAMEDATA");
            string src = !string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "static")) ? env : GameFolder.FindNear(AppContext.BaseDirectory)?.Root;
            Assert.SkipWhen(src == null, "Ultima IX game data not found");
            return src;
        }

        static GameFont Font(string root) => GameFont.Load(GamePaths.Find(root, "static/bitmap16.flx"));

        [Fact]
        public void Wraps_shipped_lines_like_the_research_measured()
        {
            string root = Root();
            var font = Font(root);
            var text = TextTable.Load(GamePaths.Find(root, "static/text.flx"));
            var counts = new int[5];
            for (int i = 0; i < text.Count; i++)
            {
                if (text[i] == null || TextTable.IsMarker(text[i])) continue;
                int n = DialogueCheck.Wrap(DialogueCheck.Convert(text[i]), font).Count;
                counts[Math.Min(n, 4)]++;
            }
            Assert.Equal(8, DialogueCheck.Wrap(DialogueCheck.Convert(text[1583]), font).Count);   // the longest line
            // The research's own measurement (glyph widths of font 0x263): 4,264 / 2,491 / 563 / 72 lines at 1 / 2 / 3 / 4+.
            Assert.InRange(counts[1], 4264 - 40, 4264 + 40);
            Assert.InRange(counts[2], 2491 - 40, 2491 + 40);
            Assert.InRange(counts[3], 563 - 20, 563 + 20);
            Assert.InRange(counts[4], 72 - 10, 72 + 10);
        }

        [Fact]
        public void Imports_a_wav_as_pcm_and_saves_with_undo()
        {
            string src = Root();
            string root = Path.Combine(Path.GetTempPath(), "u9dlg-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "static"));
                Directory.CreateDirectory(Path.Combine(root, "sound"));
                File.Copy(GamePaths.Find(src, "static/text.flx"), Path.Combine(root, "static", "text.flx"));
                File.Copy(GamePaths.Find(src, "sound/speech.flx"), Path.Combine(root, "sound", "Speech.flx"));
                var ed = DialogueEditing.Open(root);
                ed.ReadClipLengths();
                Assert.Equal(47263 / 22050.0, ed.SecondsOf(2).Value, 3);

                // A one-second 44.1 kHz stereo 16-bit WAV: becomes 22,050 mono samples of PCM.
                var stereo = new short[44100 * 2];
                for (int i = 0; i < 44100; i++) stereo[2 * i] = stereo[2 * i + 1] = (short)(8000 * Math.Sin(i * 2 * Math.PI * 440 / 44100));
                byte[] wav = MicroTalk.Wav(stereo, 44100, 2);
                ed.ImportWav(2, wav);
                var c = ed.ClipOf(2).Value;
                Assert.Equal((0, 22050, 16, 1, 44100), (c.Codec, c.Rate, c.Bits, c.Channels, c.DataLength));
                Assert.Equal(1.0, ed.SecondsOf(2).Value, 3);
                Assert.Equal(44 + 44100, ed.ClipWav(2).Length);
                ed.SetLine(2, "Not to kill me!");

                var written = ed.Save();
                Assert.Equal(2, written.Count);
                var again = DialogueEditing.Open(root);
                Assert.Equal(0, again.ClipOf(2).Value.Codec);
                Assert.Equal("Not to kill me!", again.Text[2]);
                Assert.Equal(again.ClipOf(3).Value, DialogueEditing.Open(src).ClipOf(3).Value with { });   // others untouched

                // Undo both and save: the files are the shipped ones again.
                ed.Undo(); ed.Undo();
                ed.Save();
                Assert.Equal(File.ReadAllBytes(GamePaths.Find(src, "static/text.flx")), File.ReadAllBytes(Path.Combine(root, "static", "text.flx")));
                Assert.Equal(File.ReadAllBytes(GamePaths.Find(src, "sound/speech.flx")), File.ReadAllBytes(Path.Combine(root, "sound", "Speech.flx")));
                Assert.Throws<InvalidDataException>(() => ed.ImportWav(2, new byte[] { 1, 2, 3 }));
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [Fact]
        public void Vowel_play_renames_consistently_and_undoes_in_one_step()
        {
            var ed = DialogueEditing.Open(Root());
            var names = VowelPlay.DefaultNames(ed.Text.Lines, ed.NpcNames);
            Assert.Contains("Raven", names);
            Assert.Contains("Avatar", names);
            Assert.DoesNotContain("Generic", names);
            var r = VowelPlay.Preview(ed.Text, names, 42, false);
            var raven = r.Names.Single(n => n.Was == "Raven");
            Assert.Null(raven.Stays);
            Assert.Equal(5, raven.Now.Length);
            Assert.Equal("R", raven.Now[..1]);
            Assert.Equal("v", raven.Now.Substring(2, 1));
            Assert.Equal("n", raven.Now.Substring(4, 1));
            var again = VowelPlay.Preview(ed.Text, names, 42, false);                  // same seed, same spellings
            Assert.Equal(raven.Now, again.Names.Single(n => n.Was == "Raven").Now);
            var words = VowelPlay.Words(ed.Text.Lines).Select(w => w.ToUpperInvariant()).ToHashSet();
            Assert.All(r.Names.Where(n => n.Stays == null), n => Assert.DoesNotContain(n.Now.ToUpperInvariant(), words));
            Assert.All(r.Edits, e => Assert.DoesNotMatch(@"(?<![A-Za-z])Raven(?![A-Za-z])", e.After));
            Assert.True(r.Edits.Count > 500);

            var before = ed.Text.Lines.ToArray();
            Assert.True(ed.SetLines(r.Edits));
            Assert.Contains(raven.Now, ed.Text[r.Edits.First(e => e.Before.Contains("Raven")).Line]);
            ed.Undo();
            Assert.Equal(before, ed.Text.Lines.ToArray());
            ed.Redo();
            Assert.False(ed.Text.Lines.Any(l => l != null && !TextTable.IsMarker(l) && System.Text.RegularExpressions.Regex.IsMatch(l, @"(?<![A-Za-z])Raven(?![A-Za-z])")));
        }

        [Fact]
        public void Warns_about_what_the_game_cannot_show()
        {
            var font = Font(Root());
            var blanks = Enumerable.Range(0x21, 0xFF - 0x20).Where(b => font.Blank((byte)b)).ToArray();
            Assert.Equal(new[] { 0x7F, 0x80, 0x81, 0x8D, 0x8E, 0x8F, 0x90, 0x9D, 0x9E, 0xA0, 0xA9, 0xAE, 0xB5, 0xB6 }, blanks);
            var ok = DialogueCheck.Check("Who are you?", font, 0.8);
            Assert.Empty(ok.Errors);
            Assert.Empty(ok.Warnings);
            Assert.Single(ok.Wrapped);

            var odd = DialogueCheck.Check("Buccaneer\u0092s Den costs 5 € — 中文", font, null);
            Assert.Contains(odd.Warnings, w => w.Contains("U+0092"));
            Assert.Contains(odd.Warnings, w => w.Contains("中") && w.Contains("CJK"));
            Assert.Contains(odd.Warnings, w => w.Contains("€"));                         // in cp1252, but a blank glyph
            Assert.Equal("Buccaneer’s Den", DialogueCheck.FixC1("Buccaneer\u0092s Den"));
            Assert.Contains(DialogueCheck.Check("a\u0005b", font, null).Errors, e => e.Contains("0x05"));
            Assert.NotEmpty(DialogueCheck.Check("", font, null).Errors);

            string longLine = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 60));
            var voiced = DialogueCheck.Check(longLine, font, 30);
            Assert.True(voiced.Wrapped.Count > 13);
            Assert.Contains(voiced.Errors, e => e.Contains("never shown"));
            var unvoiced = DialogueCheck.Check(longLine, font, null);
            Assert.Empty(unvoiced.Errors);
            Assert.True(unvoiced.Pages > 1);

            var twins = DialogueCheck.Check("Farewell.", font, 1, new[] { (7, "Farewell."), (8, "Hello.") });
            Assert.Contains(twins.Warnings, w => w.Contains("line 7"));
        }
    }
}
