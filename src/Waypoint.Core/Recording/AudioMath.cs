using System.Buffers.Binary;

namespace Waypoint.Core.Recording;

/// <summary>How Windows hands over captured sound: its rate, channels, and sample size.</summary>
public sealed record CaptureFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

/// <summary>Turning captured sound into the mono 16 kHz the recording holds, and how loud it is.</summary>
public static class AudioMath
{
    /// <summary>Raw captured bytes as one channel of samples from -1 to 1, every channel averaged together.</summary>
    public static float[] ToMono(ReadOnlySpan<byte> bytes, CaptureFormat format)
    {
        var size = format.BitsPerSample / 8;
        var channels = Math.Max(1, format.Channels);
        var frames = size == 0 ? 0 : bytes.Length / (size * channels);
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += Sample(bytes.Slice((frame * channels + channel) * size, size), format);
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    /// <summary>The loudest sample, from 0 to 1.</summary>
    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return Math.Min(peak, 1f);
    }

    /// <summary>Two sources added together and clipped, for the microphone and the computer's sound at once.</summary>
    public static float[] Mix(ReadOnlySpan<float> first, ReadOnlySpan<float> second)
    {
        var mixed = new float[Math.Max(first.Length, second.Length)];
        for (var index = 0; index < mixed.Length; index++)
        {
            var sum = (index < first.Length ? first[index] : 0f) + (index < second.Length ? second[index] : 0f);
            mixed[index] = Math.Clamp(sum, -1f, 1f);
        }

        return mixed;
    }

    private static float Sample(ReadOnlySpan<byte> bytes, CaptureFormat format) => (format.BitsPerSample, format.IsFloat) switch
    {
        (32, true) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        (64, true) => (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        (16, false) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        (24, false) => ((bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8)) / 2147483648f,
        (32, false) => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        (8, false) => (bytes[0] - 128) / 128f,
        _ => 0f,
    };
}

/// <summary>
/// Changes a stream of samples from one rate to another, a piece at a time. Going down, each new sample is the
/// average of the ones it covers, which keeps out the hiss that skipping samples would add. Going up, samples repeat.
/// </summary>
public sealed class Resampler
{
    private readonly double _ratio;
    private double _needed;
    private double _sum;

    public Resampler(int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromRate), "Sample rates must be above zero.");
        }

        _ratio = fromRate / (double)toRate;
        _needed = _ratio;
    }

    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new List<float>((int)(input.Length / _ratio) + 2);
        foreach (var sample in input)
        {
            var left = 1.0;
            while (left > 1e-12)
            {
                var take = Math.Min(left, _needed);
                _sum += sample * take;
                _needed -= take;
                left -= take;
                if (_needed <= 1e-9)
                {
                    output.Add((float)(_sum / _ratio));
                    _sum = 0;
                    _needed = _ratio;
                }
            }
        }

        return output.ToArray();
    }
}

/// <summary>
/// Lines up the microphone and the computer's sound, which arrive separately, and adds them together. Windows sends
/// nothing for the computer's sound while nothing plays, so once one side is further ahead than
/// <see cref="MaxLag"/>, the other is taken as silent for that stretch and the recording carries on.
/// </summary>
public sealed class TwoSourceMixer(int maxLagSamples)
{
    private readonly Queue<float> _first = new();
    private readonly Queue<float> _second = new();
    private readonly object _lock = new();

    public int MaxLag => maxLagSamples;

    public float[] AddFirst(ReadOnlySpan<float> samples) => Add(_first, samples);

    public float[] AddSecond(ReadOnlySpan<float> samples) => Add(_second, samples);

    /// <summary>Whatever is still waiting, with the shorter side padded with silence. For the end of a recording.</summary>
    public float[] Drain()
    {
        lock (_lock)
        {
            var count = Math.Max(_first.Count, _second.Count);
            return Take(count);
        }
    }

    private float[] Add(Queue<float> queue, ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (var sample in samples)
            {
                queue.Enqueue(sample);
            }

            var together = Math.Min(_first.Count, _second.Count);
            var ahead = Math.Max(_first.Count, _second.Count) - together;
            var count = ahead > maxLagSamples ? together + ahead - maxLagSamples : together;
            return Take(count);
        }
    }

    private float[] Take(int count)
    {
        var mixed = new float[count];
        for (var index = 0; index < count; index++)
        {
            var sum = (_first.TryDequeue(out var a) ? a : 0f) + (_second.TryDequeue(out var b) ? b : 0f);
            mixed[index] = Math.Clamp(sum, -1f, 1f);
        }

        return mixed;
    }
}
