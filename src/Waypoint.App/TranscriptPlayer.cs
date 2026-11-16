using System;
using NAudio.Wave;

namespace Waypoint.App;

/// <summary>
/// Plays a note's kept audio from a moment, through Windows Media Foundation, so mp3, m4a, mp4, wav, and wma all
/// play with nothing else installed. One file is open at a time, and it's let go of on Stop so it can be deleted.
/// </summary>
internal sealed class TranscriptPlayer : IDisposable
{
    private MediaFoundationReader? _reader;
    private WasapiOut? _output;

    /// <summary>Raised on the window's thread when playing reaches the end, or the device stops it.</summary>
    public event Action? Ended;

    public string? FilePath { get; private set; }

    public bool IsOpen => _reader is not null;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

    public TimeSpan? Length => _reader?.TotalTime;

    /// <summary>Opens the file if it isn't open already, moves to the moment, and plays.</summary>
    public void PlayFrom(string path, Func<TimeSpan?, TimeSpan> moment)
    {
        if (!string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase) || _reader is null)
        {
            Stop();
            _reader = new MediaFoundationReader(path);
            _output = new WasapiOut();
            _output.PlaybackStopped += OnStopped;
            _output.Init(_reader);
            FilePath = path;
        }

        _reader.CurrentTime = moment(_reader.TotalTime);
        _output!.Play();
    }

    public void Pause() => _output?.Pause();

    public void Resume() => _output?.Play();

    public void Stop()
    {
        if (_output is { } output)
        {
            output.PlaybackStopped -= OnStopped;
            output.Stop();
            output.Dispose();
        }

        _reader?.Dispose();
        _output = null;
        _reader = null;
        FilePath = null;
    }

    public void Dispose() => Stop();

    private void OnStopped(object? sender, StoppedEventArgs e) => Ended?.Invoke();
}
