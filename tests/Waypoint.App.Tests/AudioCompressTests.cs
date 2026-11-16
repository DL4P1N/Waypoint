using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Waypoint.App.Tests;

/// <summary>Kept recordings are stored as MP3 with Windows' own encoder, and still play from any moment.</summary>
public sealed class AudioCompressTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-compress-{Guid.NewGuid():N}");

    public AudioCompressTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_recording_wav_is_compressed_to_a_much_smaller_mp3_that_seeks_to_a_moment()
    {
        var wav = Path.Combine(_folder, "lecture.wav");
        var tone = new SignalGenerator(MediaFoundationAudio.SampleRate, 1) { Frequency = 440, Gain = 0.3 }.Take(TimeSpan.FromSeconds(60));
        WaveFileWriter.CreateWaveFile16(wav, tone);
        var mp3 = Path.Combine(_folder, "lecture.part.mp3");

        Assert.True(await new MediaFoundationAudio().CompressAsync(wav, mp3, CancellationToken.None));

        Assert.True(new FileInfo(mp3).Length * 4 < new FileInfo(wav).Length);
        using var reader = new MediaFoundationReader(mp3);
        Assert.InRange(reader.TotalTime.TotalSeconds, 59, 61);
        reader.CurrentTime = TimeSpan.FromSeconds(30);
        Assert.InRange(reader.CurrentTime.TotalSeconds, 29.5, 30.5);
        var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
        Assert.True(reader.Read(buffer, 0, buffer.Length) > 0);
        Assert.Contains(buffer, value => value != 0);
    }
}
