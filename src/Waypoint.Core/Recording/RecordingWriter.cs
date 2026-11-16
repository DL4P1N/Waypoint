namespace Waypoint.Core.Recording;

/// <summary>
/// Writes a recording to disk as it's made, as the 16 kHz mono 16-bit WAV Whisper reads. Every few seconds of
/// audio the header's length is brought up to date and everything is pushed to the disk itself, so a crash or
/// power loss keeps all of it up to the last flush. <see cref="WavFile.Repair(string)"/> fixes the rest.
/// Safe to call from the audio thread and the window's thread at once.
/// </summary>
public sealed class RecordingWriter : IDisposable
{
    public const int SampleRate = 16_000;
    public const int BytesPerSecond = SampleRate * 2;

    /// <summary>How much audio can be lost to a power cut, at most.</summary>
    public static readonly TimeSpan DefaultFlushEvery = TimeSpan.FromSeconds(3);

    private readonly Stream _stream;
    private readonly long _flushBytes;
    private readonly object _lock = new();
    private long _dataBytes;
    private long _sinceFlush;
    private byte[] _buffer = [];
    private bool _closed;

    public RecordingWriter(Stream stream, TimeSpan? flushEvery = null)
    {
        _stream = stream;
        _flushBytes = Math.Max(2, (long)((flushEvery ?? DefaultFlushEvery).TotalSeconds * BytesPerSecond));
        WavFile.WriteHeader(_stream, SampleRate, 1, 16, 0);
        FlushToDisk();
    }

    /// <summary>A new file that other programs can read while it's written, but not change.</summary>
    public static RecordingWriter Create(string path, TimeSpan? flushEvery = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new RecordingWriter(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16), flushEvery);
    }

    public long SamplesWritten
    {
        get
        {
            lock (_lock)
            {
                return _dataBytes / 2;
            }
        }
    }

    public TimeSpan Length => TimeSpan.FromSeconds(SamplesWritten / (double)SampleRate);

    /// <summary>Samples from -1 to 1. Anything louder is clipped.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            var bytes = samples.Length * 2;
            if (_buffer.Length < bytes)
            {
                _buffer = new byte[bytes];
            }

            for (var index = 0; index < samples.Length; index++)
            {
                var value = (short)Math.Round(Math.Clamp(samples[index], -1f, 1f) * short.MaxValue);
                _buffer[index * 2] = (byte)value;
                _buffer[index * 2 + 1] = (byte)(value >> 8);
            }

            _stream.Position = WavFile.HeaderBytes + _dataBytes;
            _stream.Write(_buffer, 0, bytes);
            _dataBytes += bytes;
            _sinceFlush += bytes;
            if (_sinceFlush >= _flushBytes)
            {
                FlushLocked();
            }
        }
    }

    /// <summary>Silence, for time when the computer played nothing and Windows sent no audio.</summary>
    public void WriteSilence(int samples)
    {
        if (samples > 0)
        {
            Write(new float[samples]);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_closed)
            {
                FlushLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            FlushLocked();
            _closed = true;
            _stream.Dispose();
        }
    }

    private void FlushLocked()
    {
        WavFile.WriteLengths(_stream, _dataBytes);
        FlushToDisk();
        _sinceFlush = 0;
    }

    private void FlushToDisk()
    {
        if (_stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
        else
        {
            _stream.Flush();
        }
    }
}
