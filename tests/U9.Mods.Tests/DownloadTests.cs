using System.IO;
using System.Linq;
using U9.Mods;
using Xunit;

namespace U9.Mods.Tests
{
    public class DownloadTests
    {
        [Fact]
        public void ChecksumsAreFoundForTheirFile()
        {
            // as U9GL 0.6.1 publishes them
            string sums = "65b7b767cb931d96b0275994e8d8a7e67a032ed04948c682d2ac7cc27e2722f6 *u9gl-0.6.1-noruntime.zip\n" +
                          "4af8dbe652876a557d7a8b0ce1ea1e454919027f9fe6d4e3fd709a004a8fff4f *u9gl-0.6.1.zip\n";
            Assert.Equal("4af8dbe652876a557d7a8b0ce1ea1e454919027f9fe6d4e3fd709a004a8fff4f", ModLibrary.ShaFor(sums, "u9gl-0.6.1.zip"));
            Assert.Equal("65b7b767cb931d96b0275994e8d8a7e67a032ed04948c682d2ac7cc27e2722f6", ModLibrary.ShaFor(sums, "u9gl-0.6.1-noruntime.zip"));
            Assert.Null(ModLibrary.ShaFor(sums, "u9gl-0.7.0.zip"));
            Assert.Equal("4af8dbe652876a557d7a8b0ce1ea1e454919027f9fe6d4e3fd709a004a8fff4f", ModLibrary.ShaFor("4AF8DBE652876A557D7A8B0CE1EA1E454919027F9FE6D4E3FD709A004A8FFF4F\n", "x.zip"));
        }

        [Fact]
        public void CatalogEntriesNeedAnIdAHashAndAnHttpsUrl()
        {
            string json = @"{ ""format"": 1, ""mods"": [
                { ""id"": ""karl.garden"", ""name"": ""Garden"", ""version"": ""1.0"", ""url"": ""https://example.org/garden.u9mod"", ""sha256"": """ + new string('a', 64) + @""", ""size"": 100 },
                { ""id"": ""Bad Id"", ""name"": ""x"", ""url"": ""https://example.org/x.u9mod"", ""sha256"": """ + new string('a', 64) + @""" },
                { ""id"": ""plain.http"", ""name"": ""y"", ""url"": ""http://example.org/y.u9mod"", ""sha256"": """ + new string('a', 64) + @""" },
                { ""id"": ""no.hash"", ""name"": ""z"", ""url"": ""https://example.org/z.u9mod"" } ] }";
            var list = ModLibrary.ParseCatalog(json);
            Assert.Equal(new[] { "karl.garden" }, list.Select(e => e.Id));
            Assert.Throws<InvalidDataException>(() => ModLibrary.ParseCatalog(@"{ ""format"": 2, ""mods"": [] }"));
        }
    }
}
