using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>
/// Reads audio and video with Windows Media Foundation, which plays mp3, m4a, mp4, wav, and wma with nothing else
/// installed, and writes the 16 kHz mono 16-bit WAV whisper-cli needs. Every channel is mixed down to one.
/// </summary>
public sealed class MediaFoundationAudio : IAudioConverter
{
    public const int SampleRate = 16_000;

    public Task<TimeSpan> ConvertAsync(string inputPath, string wavPath, CancellationToken cancellationToken) =>
        // Media Foundation wants a thread from the pool, not the window's thread.
        Task.Run(() => Convert(inputPath, wavPath, cancellationToken), cancellationToken);

    public static TimeSpan Convert(string inputPath, string wavPath, CancellationToken cancellationToken)
    {
        using var reader = new MediaFoundationReader(inputPath, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        ISampleProvider samples = new MonoMix(reader.ToSampleProvider());
        if (samples.WaveFormat.SampleRate != SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, SampleRate);
        }

        var pcm = new SampleToWaveProvider16(samples);
        var buffer = new byte[SampleRate * 2 * 4];
        long written = 0;
        using (var writer = new WaveFileWriter(wavPath, pcm.WaveFormat))
        {
            int read;
            while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, read);
                written += read;
            }
        }

        if (written == 0)
        {
            File.Delete(wavPath);
            throw new InvalidDataException("There was no sound in it");
        }

        return TimeSpan.FromSeconds(written / (double)(SampleRate * 2));
    }

    /// <summary>MP3 plays and seeks anywhere in Windows, and its encoder comes with Windows, so nothing is installed.</summary>
    public string CompressedExtension => ".mp3";

    /// <summary>Speech at 32 kHz mono and 48 kbps, about 22 MB an hour against 115 MB for the 16 kHz WAV.</summary>
    public const int CompressedRate = 32_000;

    public const int CompressedBitRate = 48_000;

    public Task<bool> CompressAsync(string wavPath, string outputPath, CancellationToken cancellationToken) =>
        Task.Run(() => Compress(wavPath, outputPath, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes an MP3 of the audio with Media Foundation's own encoder. Throws when Windows has none, and the caller
    /// keeps the WAV instead.
    /// </summary>
    public static bool Compress(string wavPath, string outputPath, CancellationToken cancellationToken)
    {
        MediaFoundationApi.Startup();
        using var reader = new MediaFoundationReader(wavPath, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        ISampleProvider samples = new MonoMix(reader.ToSampleProvider());
        if (samples.WaveFormat.SampleRate != CompressedRate)
        {
            samples = new WdlResamplingSampleProvider(samples, CompressedRate);
        }

        MediaFoundationEncoder.EncodeToMp3(new Stoppable(new SampleToWaveProvider16(samples), cancellationToken), outputPath, CompressedBitRate);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>Stops an encode partway when asked, as the encoder itself can't be canceled.</summary>
    private sealed class Stoppable(IWaveProvider source, CancellationToken cancellationToken) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return source.Read(buffer, offset, count);
        }
    }

    /// <summary>Averages every channel into one, for any number of channels.</summary>
    private sealed class MonoMix : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = [];

        public MonoMix(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_channels == 1)
            {
                return _source.Read(buffer, offset, count);
            }

            var needed = count * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            var read = _source.Read(_buffer, 0, needed);
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _buffer[frame * _channels + channel];
                }

                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}
