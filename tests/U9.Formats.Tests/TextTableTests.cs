using System.IO;
using System.Linq;
using U9.Formats.Text;
using Xunit;

namespace U9.Formats.Tests
{
    public class TextTableTests
    {
        [Fact]
        public void Reads_the_conversation_lines_as_utf16()
        {
            var t = TextTable.Load(GameData.PathOf("static/text.flx"));
            Assert.Equal(7656, t.Count);
            Assert.True(TextTable.IsMarker(t[0]));
            Assert.Equal("Exferlem", TextTable.MarkerName(t[1]));
            Assert.Equal("To not kill me!  To go!", t[2]);
            Assert.Equal("Who are you?", t[9]);
        }

        [Fact]
        public void Writes_any_utf16_text_and_round_trips()
        {
            var path = GameData.PathOf("static/text.flx");
            var t = TextTable.Load(path);
            Assert.Equal(File.ReadAllBytes(path), t.ToBytes());                 // unchanged: byte for byte
            t.Set(9, "Wer bist du? Qui êtes-vous? あなたは誰ですか？");
            var back = TextTable.Read(t.ToBytes());
            Assert.Equal("Wer bist du? Qui êtes-vous? あなたは誰ですか？", back[9]);
            Assert.Equal(t[2], back[2]);
            t.Set(9, "Who are you?");
            Assert.Equal(TextTable.Read(File.ReadAllBytes(path))[9], TextTable.Read(t.ToBytes())[9]);
        }

        [Fact]
        public void Decodes_microtalk_speech_like_titan()
        {
            var rec = SpeechTable.ReadRecord(GameData.PathOf("sound/Speech.flx"), 2);
            var samples = MicroTalk.Decode(rec, SpeechClip.HeaderSize);
            Assert.Equal(47263, samples.Length);                                // 2.14 s at 22,050 Hz
            Assert.Equal(new short[] { -8682, -8943, -9034, -8833, -8892, -9192, -9398, -9716 }, samples.Skip(1000).Take(8).ToArray());
            var pcm = new byte[samples.Length * 2];
            System.Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            // The SHA-256 of TITAN's (titan-ultima) decode of the same clip: bit for bit the same.
            Assert.Equal("1997d23c4d07754ba6f3d2613fd9a44f1678eaaf420511ebe7d303815c2f96f8", System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pcm)));
            var wav = MicroTalk.Wav(samples, 22050);
            Assert.Equal(44 + pcm.Length, wav.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        }

        [Fact]
        public void Speech_records_pair_with_lines()
        {
            var clips = SpeechTable.ReadHeaders(GameData.PathOf("sound/Speech.flx"));
            Assert.Equal(7010, clips.Count);
            Assert.All(clips, c => Assert.Equal(c.Record, c.Line));
            var c2 = clips.First(c => c.Record == 2);
            Assert.Equal(("Exferlem_07143.umt", "Exferlem", 22050, 16, 1, 2), (c2.Name, c2.Speaker, c2.Rate, c2.Bits, c2.Channels, c2.Codec));
            Assert.All(clips, c => Assert.Equal(c.RecordLength, c.DataLength + SpeechClip.HeaderSize));
        }
    }
}
