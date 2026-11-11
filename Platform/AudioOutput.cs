using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ArcademiaGameLauncher.Platform
{
    internal static class AudioOutput
    {
        public static WaveStream CreateMp3Reader(Stream stream) =>
            new Mp3FileReaderBase(stream, format => new NLayer.NAudioSupport.Mp3FrameDecompressor(format));

        public static async Task PlayAsync(ISampleProvider provider, CancellationToken ct)
        {
            if (!Sdl.EnsureInitialised(Sdl.InitAudio))
                throw new InvalidOperationException($"Audio is unavailable: {Sdl.LastError}");

            var samples = ReadAll(provider);
            var desired = new Sdl.AudioSpec
            {
                freq = provider.WaveFormat.SampleRate,
                format = Sdl.AudioF32,
                channels = (byte)provider.WaveFormat.Channels,
                samples = 4096,
            };

            uint device;
            lock (Sdl.Gate)
            {
                device = Sdl.SDL_OpenAudioDevice(0, 0, ref desired, out _, 0);
                if (device == 0)
                    throw new InvalidOperationException($"Could not open the audio device: {Sdl.LastError}");

                Sdl.SDL_QueueAudio(device, samples, (uint)(samples.Length * sizeof(float)));
                Sdl.SDL_PauseAudioDevice(device, 0);
            }

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    uint queued;
                    lock (Sdl.Gate)
                        queued = Sdl.SDL_GetQueuedAudioSize(device);
                    if (queued == 0)
                        break;
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                }

                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (Sdl.Gate)
                {
                    Sdl.SDL_ClearQueuedAudio(device);
                    Sdl.SDL_CloseAudioDevice(device);
                }
            }
        }

        private static float[] ReadAll(ISampleProvider provider)
        {
            var all = new List<float>();
            var buffer = new float[provider.WaveFormat.SampleRate * provider.WaveFormat.Channels];
            int read;
            while ((read = provider.Read(buffer.AsSpan())) > 0)
                all.AddRange(new ArraySegment<float>(buffer, 0, read));
            return [.. all];
        }
    }
}
