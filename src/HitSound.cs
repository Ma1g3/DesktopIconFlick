using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopIconFlick
{
    internal sealed class HitSound : IDisposable
    {
        private const uint Async = 0x0001, NoDefault = 0x0002, Memory = 0x0004;
        private GCHandle wave = GCHandle.Alloc(CreateWave(), GCHandleType.Pinned);
        private bool playing;

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern bool PlaySoundW(IntPtr sound, IntPtr module, uint flags);

        public void Play()
        {
            playing = PlaySoundW(wave.AddrOfPinnedObject(), IntPtr.Zero, Async | NoDefault | Memory) || playing;
        }

        public void Stop()
        {
            if (playing)
                playing = !PlaySoundW(IntPtr.Zero, IntPtr.Zero, 0);
        }

        public void Dispose()
        {
            Stop();
            // Keep the buffer pinned if the device has not confirmed that playback stopped.
            if (!playing && wave.IsAllocated)
                wave.Free();
        }

        private static byte[] CreateWave()
        {
            const int rate = 22050, sampleCount = rate / 20, bytesPerSample = sizeof(short);
            int byteCount = sampleCount * bytesPerSample;
            using (var stream = new MemoryStream(44 + byteCount))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46464952); // RIFF
                writer.Write(36 + byteCount);
                writer.Write(0x45564157); // WAVE
                writer.Write(0x20746D66); // fmt
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write((short)1); // Mono
                writer.Write(rate);
                writer.Write(rate * bytesPerSample);
                writer.Write((short)bytesPerSample);
                writer.Write((short)(bytesPerSample * 8));
                writer.Write(0x61746164); // data
                writer.Write(byteCount);
                double phase = 0;
                for (int index = 0; index < sampleCount; index++)
                {
                    double progress = (double)index / (sampleCount - 1);
                    double attack = Math.Min(1, progress * 50);
                    double decay = 1 - progress;
                    phase += (1450 - 550 * progress) * (2 * Math.PI / rate);
                    writer.Write((short)(Math.Sin(phase) * attack * decay * decay * 7200));
                }
                return stream.ToArray();
            }
        }
    }
}
