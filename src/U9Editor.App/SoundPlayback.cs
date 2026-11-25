using System;
using System.Runtime.InteropServices;

namespace U9Editor.App
{
    /// <summary>Plays a WAV held in memory (Windows' PlaySound; elsewhere it says it cannot).</summary>
    static class SoundPlayback
    {
        [DllImport("winmm.dll", SetLastError = true)]
        static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
        [DllImport("winmm.dll", SetLastError = true)]
        static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
        static GCHandle playing;

        public static bool Available => OperatingSystem.IsWindows();

        public static bool Play(byte[] wav)
        {
            if (!Available) return false;
            Stop();
            // The buffer must stay put while Windows plays it asynchronously.
            playing = GCHandle.Alloc(wav, GCHandleType.Pinned);
            return PlaySound(playing.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_MEMORY);
        }

        public static void Stop()
        {
            if (!Available) return;
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            if (playing.IsAllocated) playing.Free();
        }
    }
}
