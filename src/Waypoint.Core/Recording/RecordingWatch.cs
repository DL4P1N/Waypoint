namespace Waypoint.Core.Recording;

/// <summary>Something that could cost the rest of a lecture. Each is said once per recording.</summary>
public enum RecordingWarning
{
    LowBattery,
    LowDisk,
    SilentMicrophone,
    MicrophoneMuted,
    InputLost,
}

/// <summary>The battery right now. Percent is null when the PC has no battery or Windows can't tell.</summary>
public sealed record PowerReading(bool OnBattery, int? BatteryPercent)
{
    public static readonly PowerReading PluggedIn = new(false, null);
}

/// <summary>
/// Keeps an eye on a recording: battery, disk space, and whether the microphone hears anything. A warning is
/// raised the first time its problem shows up and never again for the same recording, so it never nags. It stays
/// in <see cref="Showing"/> while the problem lasts, for the recording indicator.
/// </summary>
public sealed class RecordingWatch
{
    public const int LowBatteryPercent = 20;

    /// <summary>About nine hours of recording. Well past a lecture, and enough room left for Windows too.</summary>
    public const long LowDiskBytes = 1_000_000_000;

    /// <summary>Below this the microphone is taken to be hearing nothing at all. Quiet speech is far above it.</summary>
    public const float SilentPeak = 0.002f;

    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    private readonly bool _listensToMicrophone;
    private readonly HashSet<RecordingWarning> _raised = [];
    private readonly HashSet<RecordingWarning> _showing = [];
    private readonly object _lock = new();
    private DateTime _lastHeard;

    public RecordingWatch(DateTime startedAt, bool listensToMicrophone)
    {
        _lastHeard = startedAt;
        _listensToMicrophone = listensToMicrophone;
    }

    /// <summary>The problems going on right now, in order.</summary>
    public IReadOnlyList<RecordingWarning> Showing
    {
        get
        {
            lock (_lock)
            {
                return _showing.Order().ToList();
            }
        }
    }

    /// <summary>The microphone's loudest sample in a piece of audio, from the audio thread.</summary>
    public void Heard(float peak, DateTime now)
    {
        if (peak < SilentPeak)
        {
            return;
        }

        lock (_lock)
        {
            _lastHeard = now;
        }
    }

    /// <summary>The PC was asleep this long. Nothing could be heard then, so it doesn't count toward a silent microphone.</summary>
    public void Slept(TimeSpan asleep)
    {
        if (asleep <= TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            _lastHeard += asleep;
        }
    }

    /// <summary>Checks everything, and returns only the warnings raised for the first time just now.</summary>
    public IReadOnlyList<RecordingWarning> Check(DateTime now, PowerReading power, long? freeBytes, bool microphoneMuted = false)
    {
        lock (_lock)
        {
            Set(RecordingWarning.LowBattery, power.OnBattery && power.BatteryPercent is { } percent && percent <= LowBatteryPercent);
            Set(RecordingWarning.LowDisk, freeBytes is { } free && free < LowDiskBytes);
            Set(RecordingWarning.MicrophoneMuted, _listensToMicrophone && microphoneMuted);
            Set(RecordingWarning.SilentMicrophone, _listensToMicrophone && !microphoneMuted && now - _lastHeard >= SilenceLimit);
            return TakeNew();
        }
    }

    /// <summary>The sound device stopped or was unplugged. Returns true the first time.</summary>
    public bool InputLost()
    {
        lock (_lock)
        {
            _showing.Add(RecordingWarning.InputLost);
            return _raised.Add(RecordingWarning.InputLost);
        }
    }

    /// <summary>What to tell the user, in plain words.</summary>
    public static string Describe(RecordingWarning warning) => warning switch
    {
        RecordingWarning.LowBattery => "The battery is low. Plug in the charger so the recording isn't cut off.",
        RecordingWarning.LowDisk => "The disk is almost full. Free up some space so the recording can keep going.",
        RecordingWarning.SilentMicrophone => "The microphone hasn't picked up any sound for a minute. Check that it's on and not muted.",
        RecordingWarning.MicrophoneMuted => "The microphone is muted in Windows, so nothing is being recorded. Unmute it to keep recording.",
        _ => "The sound input stopped working, maybe because it was unplugged. What was recorded so far is saved. Stop and start again to keep recording.",
    };

    private void Set(RecordingWarning warning, bool on)
    {
        if (on)
        {
            _showing.Add(warning);
        }
        else if (warning != RecordingWarning.InputLost)
        {
            _showing.Remove(warning);
        }
    }

    private List<RecordingWarning> TakeNew() => _showing.Order().Where(_raised.Add).ToList();
}
