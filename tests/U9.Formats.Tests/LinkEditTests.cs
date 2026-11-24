using System;
using System.IO;
using System.Linq;
using U9.Formats.Objects;
using U9.Formats.Triggers;
using Xunit;

namespace U9.Formats.Tests
{
    public class LinkEditTests
    {
        static readonly string Nonfixed9 = Path.Combine("runtime", "nonfixed.9"), Nonfixed4 = Path.Combine("runtime", "nonfixed.4");

        [Fact]
        public void AnObjectFromAnotherMapKeepsItsPropertiesAndGetsANewLink()
        {
            GameData.Require();
            var britannia = ObjectMap.Read(File.ReadAllBytes(GameData.PathOf(Nonfixed9)), ObjectFileKind.Nonfixed);
            var zone = britannia.Objects.First(o => o.Type == 1111 && o.Properties != null && britannia.CanLeaveChunk(o));
            var seg = britannia.Data.AsSpan(britannia.DataStart + zone.PropertyOffset, 16).ToArray();

            var moonglow = ObjectMap.Read(File.ReadAllBytes(GameData.PathOf(Nonfixed4)), ObjectFileKind.Nonfixed);
            int count = moonglow.Objects.Count;
            var n = moonglow.Add(zone.Raw, seg, 200 * 128, 210 * 128, 1800);
            moonglow.SetLink(n, 4321);
            moonglow.SetPropertyParam(n, 2, 9999);

            var back = ObjectMap.Read(moonglow.ToBytes(), ObjectFileKind.Nonfixed);
            Assert.Equal(count + 1, back.Objects.Count);
            var b = back.Objects.Single(o => o.Link == 4321);
            Assert.Equal((1111, 200 * 128, 210 * 128, 1800), (b.Type, b.MapX, b.MapY, b.Z));
            Assert.Equal(zone.Properties.Ids, b.Properties.Ids);
            Assert.Equal(9999, b.Properties.Params[2]);
            Assert.Equal(zone.Properties.Params[0], b.Properties.Params[0]);
        }

        [Fact]
        public void AScriptEditSurvivesASave()
        {
            GameData.Require();
            string copy = Path.Combine(Path.GetTempPath(), "triggers-" + Guid.NewGuid().ToString("N") + ".flx");
            File.Copy(GameData.PathOf(Path.Combine("static", "TRIGGERS.FLX")), copy);
            try
            {
                var t = TriggerScripts.Load(copy);
                var before = t[2053].ToArray();
                int slot = t.FreeSlot();
                Assert.False(t.IsUsed(slot));
                t.Set(slot, TriggerScripts.ChangeMapScript(77, 1134, 89));
                t.Set(2053, TriggerScripts.ChangeMapScript(5, 1134, 12));
                t.Save();

                var back = TriggerScripts.Load(copy);
                Assert.Equal(new[] { 0x1011, 0x101F, 0x10FF }, back[slot].Select(o => (int)o.Op));
                Assert.Equal((77, 89), (back[slot][0].B, back[slot][1].B & 0xFF));
                Assert.Equal(12, back[2053][1].B & 0xFF);
                Assert.Equal(t[100].Select(o => o.ToString()), back[100].Select(o => o.ToString()));
                Assert.NotEqual(before.Select(o => o.ToString()), back[2053].Select(o => o.ToString()));
            }
            finally { File.Delete(copy); }
        }
    }
}
