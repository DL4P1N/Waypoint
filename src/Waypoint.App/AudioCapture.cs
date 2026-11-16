using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Waypoint.Core.Recording;

namespace Waypoint.App;

/// <summary>
/// Listens to the microphone, the computer's own sound (WASAPI loopback, for Zoom and other calls), or both,
/// through Windows audio. Every piece of sound is turned into mono 16 kHz and handed on as it arrives, from the
/// audio thread. Only made when the user starts a recording, and stopped with it.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly AudioInput _input;
    private readonly Action<float[]> _microphone;
    private readonly Action<float[]> _computer;
    private readonly Action<string> _stopped;
    private MMDevice? _micDevice;
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopback;
    private bool _stopping;

    /// <param name="stopped">Runs when Windows stops a capture by itself, such as when the device is unplugged.</param>
    public AudioCapture(AudioInput input, Action<float[]> microphone, Action<float[]> computer, Action<string> stopped)
    {
        _input = input;
        _microphone = microphone;
        _computer = computer;
        _stopped = stopped;
    }

    /// <summary>Whether Windows has the microphone muted. False when it can't tell.</summary>
    public bool MicrophoneMuted
    {
        get
        {
            try
            {
                return _micDevice?.AudioEndpointVolume.Mute == true;
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void Start()
    {
        if (_input is AudioInput.Microphone or AudioInput.Both)
        {
            _micDevice = WasapiCapture.GetDefaultCaptureDevice();
            _micCapture = new WasapiCapture(_micDevice);
            Wire(_micCapture, _microphone);
        }

        if (_input is AudioInput.ComputerAudio or AudioInput.Both)
        {
            _loopback = new WasapiLoopbackCapture();
            Wire(_loopback, _computer);
        }

        _micCapture?.StartRecording();
        _loopback?.StartRecording();
    }

    public void Dispose()
    {
        _stopping = true;
        Stop(_micCapture);
        Stop(_loopback);
        _micCapture = null;
        _loopback = null;
        _micDevice?.Dispose();
        _micDevice = null;
    }

    public static CaptureFormat FormatOf(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubFormat);
        return new CaptureFormat(format.SampleRate, format.Channels, format.BitsPerSample, isFloat);
    }

    private void Wire(WasapiCapture capture, Action<float[]> target)
    {
        var format = FormatOf(capture.WaveFormat);
        var resampler = new Resampler(format.SampleRate, RecordingWriter.SampleRate);
        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            var mono = AudioMath.ToMono(e.Buffer.AsSpan(0, e.BytesRecorded), format);
            target(format.SampleRate == RecordingWriter.SampleRate ? mono : resampler.Process(mono));
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (!_stopping)
            {
                if (e.Exception is { } problem)
                {
                    Waypoint.Core.PlainProblem.Report(problem);
                }

                _stopped("Windows stopped the sound input.");
            }
        };
    }

    private static void Stop(WasapiCapture? capture)
    {
        if (capture is null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Already stopped, such as after the device was unplugged.
        }

        capture.Dispose();
    }
}
