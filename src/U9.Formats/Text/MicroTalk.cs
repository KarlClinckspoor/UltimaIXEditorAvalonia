using System;
using System.Buffers.Binary;

namespace U9.Formats.Text
{
    /// <summary>
    /// EA MicroTalk (UTK) speech decoder, for sound/Speech.flx records of codec 2.
    ///
    /// Ported to C# from theGreyWanderer's TITAN toolkit (titan-ultima, src/titan/u9/microtalk.py, MIT; see
    /// THIRD-PARTY-NOTICES.md), itself a port of vgmstream's utkdec (UTK_EA path), which derives from Andrew D'Addesio's
    /// public-domain utkencode. All arithmetic is single precision like the reference: the LPC filter runs across frames,
    /// so double precision would drift.
    ///
    /// The payload (a record's data after its 0x3C-byte header) starts with a u32 sample count; the bitstream follows.
    /// Each frame gives 432 samples; the last is cut to the count.
    /// </summary>
    public static class MicroTalk
    {
        public const int FrameSamples = 432;

        static readonly float[] RcTable =
        {
            +0.000000f, -0.996776f, -0.990327f, -0.983879f, -0.977431f, -0.970982f, -0.964534f, -0.958085f, -0.951637f, -0.930754f,
            -0.904960f, -0.879167f, -0.853373f, -0.827579f, -0.801786f, -0.775992f, -0.750198f, -0.724405f, -0.698611f, -0.670635f,
            -0.619048f, -0.567460f, -0.515873f, -0.464286f, -0.412698f, -0.361111f, -0.309524f, -0.257937f, -0.206349f, -0.154762f,
            -0.103175f, -0.051587f, +0.000000f, +0.051587f, +0.103175f, +0.154762f, +0.206349f, +0.257937f, +0.309524f, +0.361111f,
            +0.412698f, +0.464286f, +0.515873f, +0.567460f, +0.619048f, +0.670635f, +0.698611f, +0.724405f, +0.750198f, +0.775992f,
            +0.801786f, +0.827579f, +0.853373f, +0.879167f, +0.904960f, +0.930754f, +0.951637f, +0.958085f, +0.964534f, +0.970982f,
            +0.977431f, +0.983879f, +0.990327f, +0.996776f,
        };

        static readonly byte[] Normal =
        {
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 21,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 25,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 22,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 0,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 21,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 26,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 22,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 2,
        };

        static readonly byte[] LargePulse =
        {
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 27,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 1,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 28,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 3,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 27,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 1,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 28,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 3,
        };

        static readonly byte[][] Codebooks = { Normal, LargePulse };

        // (next model, code size, pulse value)
        static readonly (int Next, int Size, float Pulse)[] Commands =
        {
            (1, 8, 0f), (1, 7, 0f), (0, 8, 0f), (0, 7, 0f), (0, 2, 0f), (0, 2, -1f), (0, 2, +1f), (0, 3, -1f), (0, 3, +1f),
            (1, 4, -2f), (1, 4, +2f), (1, 3, -2f), (1, 3, +2f), (1, 5, -3f), (1, 5, +3f), (1, 4, -3f), (1, 4, +3f),
            (1, 6, -4f), (1, 6, +4f), (1, 5, -4f), (1, 5, +4f), (1, 7, -5f), (1, 7, +5f), (1, 6, -5f), (1, 6, +5f),
            (1, 8, -6f), (1, 8, +6f), (1, 7, -6f), (1, 7, +6f),
        };

        sealed class Bits
        {
            readonly byte[] data; int pos; readonly int end;
            uint value; int count;
            public Bits(byte[] d, int start) { data = d; pos = start; end = d.Length; }
            int Next() => pos < end ? data[pos++] : 0;
            public void Init() { if (count == 0) { value = (uint)Next(); count = 8; } }
            public int Peek(int n) => (int)(value & ((1u << n) - 1));
            public int Read(int n)
            {
                int r = (int)(value & ((1u << n) - 1));
                value >>= n;
                count -= n;
                if (count < 8) { value |= (uint)Next() << count; count += 8; }
                return r;
            }
        }

        sealed class State
        {
            public Bits Br;
            public bool Parsed, Reduced;
            public int Threshold;
            public readonly float[] FixedGains = new float[64];
            public readonly float[] Rc = new float[12];
            public readonly float[] History = new float[12];
            public readonly float[] Sub = new float[324 + 432];   // [0,324) adaptive codebook, then the frame's samples
        }

        /// <summary>The sample count a payload announces.</summary>
        public static int SampleCount(ReadOnlySpan<byte> payload) => payload.Length < 4 ? 0 : BinaryPrimitives.ReadInt32LittleEndian(payload);

        /// <summary>Decodes a payload (sample count + bitstream) to 16-bit samples.</summary>
        public static short[] Decode(byte[] payload, int offset = 0)
        {
            if (payload.Length - offset < 4) throw new System.IO.InvalidDataException("MicroTalk payload too small");
            int total = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            if (total < 0 || total > 50_000_000) throw new System.IO.InvalidDataException($"MicroTalk sample count {total} is out of range");
            var s = new State { Br = new Bits(payload, offset + 4) };
            var output = new short[total];
            int done = 0;
            while (done < total)
            {
                Frame(s);
                int take = Math.Min(FrameSamples, total - done);
                for (int i = 0; i < take; i++)
                {
                    float x = s.Sub[324 + i];
                    long r = (long)(x >= 0 ? x + 0.5f : x - 0.5f);
                    output[done + i] = (short)Math.Clamp(r, short.MinValue, short.MaxValue);
                }
                done += take;
            }
            return output;
        }

        static void Header(State s)
        {
            var br = s.Br;
            s.Reduced = br.Read(1) == 1;
            int thre = br.Read(4), gain = br.Read(4), mult = br.Read(6);
            s.Threshold = 32 - thre;
            s.FixedGains[0] = 8.0f * (1 + gain);
            float m = 1.04f + mult * 0.001f;
            for (int i = 1; i < 64; i++) s.FixedGains[i] = s.FixedGains[i - 1] * m;
        }

        static void Excitation(State s, bool multipulse, float[] o, int off, int stride)
        {
            var br = s.Br;
            int i = 0;
            if (multipulse)
            {
                int model = 0;
                while (i < 108)
                {
                    int cmd = Codebooks[model][br.Peek(8)];
                    var c = Commands[cmd];
                    model = c.Next;
                    br.Read(c.Size);
                    if (cmd > 3) { o[off + i] = c.Pulse; i += stride; }
                    else if (cmd > 1)
                    {
                        int count = 7 + br.Read(6);
                        if (i + count * stride > 108) count = (108 - i) / stride;
                        while (count-- > 0) { o[off + i] = 0f; i += stride; }
                    }
                    else
                    {
                        int x = 7;
                        while (br.Read(1) != 0) x++;
                        if (br.Read(1) == 0) x = -x;
                        o[off + i] = x;
                        i += stride;
                    }
                }
            }
            else
            {
                while (i < 108)
                {
                    int code = br.Peek(2);
                    float v; int bits;
                    if (code == 0 || code == 2) { v = 0f; bits = 1; }
                    else if (code == 1) { v = -2f; bits = 2; }
                    else { v = 2f; bits = 2; }
                    br.Read(bits);
                    o[off + i] = v;
                    i += stride;
                }
            }
        }

        static float[] RcToLpc(float[] rc)
        {
            var lpc = new float[12];
            var tmp1 = new float[12];
            var tmp2 = new float[12];
            for (int i = 10; i >= 0; i--) tmp2[i + 1] = rc[i];
            tmp2[0] = 1.0f;
            for (int i = 0; i < 12; i++)
            {
                float x = -(rc[11] * tmp2[11]);
                for (int j = 10; j >= 0; j--)
                {
                    x = x - rc[j] * tmp2[j];
                    tmp2[j + 1] = x * rc[j] + tmp2[j];
                }
                tmp2[0] = x;
                tmp1[i] = x;
                for (int j = 0; j < i; j++) x = x - tmp1[i - 1 - j] * lpc[j];
                lpc[i] = x;
            }
            return lpc;
        }

        static void Synthesis(State s, int offset, int blocks)
        {
            var lpc = RcToLpc(s.Rc);
            var h = s.History;
            for (int b = 0; b < blocks; b++)
                for (int j = 0; j < 12; j++)
                {
                    float x = s.Sub[324 + offset];
                    for (int k = 0; k < j; k++) x = x + lpc[k] * h[k - j + 12];
                    for (int k = j; k < 12; k++) x = x + lpc[k] * h[k - j];
                    h[11 - j] = x;
                    s.Sub[324 + offset] = x;
                    offset++;
                }
        }

        static void Interpolate(float[] e, int b)
        {
            for (int i = 0; i < 108; i += 2)
            {
                float t1 = (e[b + i - 5] + e[b + i + 5]) * 0.01803268f;
                float t2 = (e[b + i - 3] + e[b + i + 3]) * 0.11459156f;
                float t3 = (e[b + i - 1] + e[b + i + 1]) * 0.59738597f;
                e[b + i] = t1 - t2 + t3;
            }
        }

        static void Frame(State s)
        {
            var br = s.Br;
            bool multipulse = false;
            var exc = new float[5 + 108 + 5];
            var rcDelta = new float[12];
            br.Init();
            if (!s.Parsed) { Header(s); s.Parsed = true; }
            for (int i = 0; i < 12; i++)
            {
                int idx;
                if (i == 0) { idx = br.Read(6); if (idx < s.Threshold) multipulse = true; }
                else if (i < 4) idx = br.Read(6);
                else idx = 16 + br.Read(5);
                rcDelta[i] = (RcTable[idx] - s.Rc[i]) * 0.25f;
            }
            for (int i = 0; i < 4; i++)
            {
                int lag = br.Read(8), pv = br.Read(4), gi = br.Read(6);
                float pitchGain = pv / 15.0f;
                float fixedGain = s.FixedGains[gi];
                if (!s.Reduced) Excitation(s, multipulse, exc, 5, 1);
                else
                {
                    int align = br.Read(1), zero = br.Read(1);
                    Excitation(s, multipulse, exc, 5 + align, 2);
                    if (zero != 0) for (int j = 0; j < 54; j++) exc[5 + (1 - align) + 2 * j] = 0f;
                    else
                    {
                        Array.Clear(exc, 0, 5);
                        Array.Clear(exc, 5 + 108, 5);
                        Interpolate(exc, 5 + (1 - align));
                        fixedGain *= 0.5f;
                    }
                }
                for (int j = 0; j < 108; j++)
                {
                    int idx = 108 * i + 216 - lag + j;
                    if (idx < 0) idx = 0;
                    s.Sub[324 + 108 * i + j] = fixedGain * exc[5 + j] + pitchGain * s.Sub[idx];
                }
            }
            Array.Copy(s.Sub, 324 + 108, s.Sub, 0, 324);
            for (int i = 0; i < 4; i++)
            {
                for (int k = 0; k < 12; k++) s.Rc[k] += rcDelta[k];
                Synthesis(s, 12 * i, i < 3 ? 1 : 33);
            }
        }

        /// <summary>A 16-bit PCM WAV file of the samples.</summary>
        public static byte[] Wav(short[] samples, int rate, int channels = 1)
        {
            int data = samples.Length * 2;
            var b = new byte[44 + data];
            void S(int o, string t) { for (int i = 0; i < 4; i++) b[o + i] = (byte)t[i]; }
            S(0, "RIFF"); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 36 + data); S(8, "WAVE");
            S(12, "fmt "); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), (short)channels); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), rate);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), rate * channels * 2); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), (short)(channels * 2));
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), 16);
            S(36, "data"); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), data);
            Buffer.BlockCopy(samples, 0, b, 44, data);
            return b;
        }
    }
}
