using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace U9.Formats.Tests
{
    /// <summary>
    /// Finds the Ultima IX install copy: the U9_GAMEDATA environment variable, or a gamedata folder (with a
    /// static subfolder) in the test's folder or any folder above it. Tests that need it skip when it is missing.
    /// </summary>
    static class GameData
    {
        static readonly Lazy<string> root = new Lazy<string>(Find);

        public static string Root => root.Value;

        static string Find()
        {
            string env = Environment.GetEnvironmentVariable("U9_GAMEDATA");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "static"))) return env;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string g = Path.Combine(d.FullName, "gamedata");
                if (Directory.Exists(Path.Combine(g, "static"))) return g;
            }
            return null;
        }

        public static string Require()
        {
            Assert.SkipWhen(Root == null, "Ultima IX game data not found: set U9_GAMEDATA or put the install in gamedata/");
            return Root;
        }

        /// <summary>Files in a game data subfolder whose name matches, as paths relative to the game data root.</summary>
        public static IEnumerable<string> Files(string subfolder, Func<string, bool> match)
        {
            if (Root == null) return new[] { "(no game data)" };
            return Directory.GetFiles(Path.Combine(Root, subfolder))
                .Where(f => match(Path.GetFileName(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => Path.Combine(subfolder, Path.GetFileName(f)));
        }

        public static string PathOf(string relative)
        {
            Require();
            return Path.Combine(Root, relative);
        }

        public static byte[] Sha256(string path)
        {
            using var fs = File.OpenRead(path);
            return SHA256.HashData(fs);
        }
    }

    /// <summary>A write-only stream that hashes what goes through it, so huge archives can be compared without a copy.</summary>
    sealed class HashingStream : Stream
    {
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { hash.AppendData(buffer, offset, count); length += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); length += buffer.Length; }
        public byte[] Finish() => hash.GetHashAndReset();
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }
}
