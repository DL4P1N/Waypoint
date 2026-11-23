using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using Waypoint.Core.Background;
using Waypoint.Core.Discord;

namespace Waypoint.App;

/// <summary>
/// What closing goes through, in order, kept apart from <see cref="App"/> so it can be run without starting Waypoint.
/// A second copy exits before any of this is set up, so each part is null or does nothing when it wasn't set up.
/// Each step is done on its own, so a problem with one is logged and the rest still happen.
/// </summary>
internal sealed class AppClosing
{
    /// <summary>Set first, so install.ps1 waits for the rest rather than stopping this copy partway through saving.</summary>
    public EventWaitHandle? Closing { get; init; }

    /// <summary>Waits up to the limit for changes on their way to the server, and gives those that didn't finish.</summary>
    public Func<TimeSpan, IReadOnlyList<string>>? FinishPageWork { get; init; }

    /// <summary>How long closing waits for changes still on their way to the server.</summary>
    public TimeSpan PageWorkLimit { get; init; }

    public Action? SaveDrafts { get; init; }

    public Action? SavePlace { get; init; }

    public Action? FinishNoteSync { get; init; }

    /// <summary>The pages' saved view waits a moment after each change before it's written, so what's left is written now.</summary>
    public Action? FlushSavedReads { get; init; }

    /// <summary>Finishes the file, so what was recorded is offered as notes next start.</summary>
    public Action? FinishRecording { get; init; }

    public WaypointWorker? Worker { get; init; }

    /// <summary>What Discord events handed to the window thread, run before the dispatcher stops.</summary>
    public Action? RunPosted { get; init; }

    /// <summary>Takes nothing new from here on: no timer runs.</summary>
    public Action? StopIntake { get; init; }

    /// <summary>The tray's menu is off and the tray hidden, so no dialog opens from it. Done even if stopping the timers failed.</summary>
    public Action? HideTray { get; init; }

    /// <summary>How long closing waits for Discord's events and for the worker's work.</summary>
    public TimeSpan DrainLimit { get; init; } = DiscordGateway.EventDrainLimit;

    public Action? DisposeTranscription { get; init; }

    /// <summary>The tray goes and the single-instance lock is let go, last of all.</summary>
    public Action? Release { get; init; }

    public required Action<Exception> LogError { get; init; }

    public required Action<string> LogText { get; init; }

    /// <summary>
    /// Runs every step on the window thread behind <paramref name="dispatcher"/>, which keeps running while the
    /// worker's work is waited for, since work started from it carries on there.
    /// </summary>
    public void Run(Dispatcher dispatcher)
    {
        void Step(Action step)
        {
            try
            {
                step();
            }
            catch (Exception exception)
            {
                LogError(exception);
            }
        }

        Step(() => Closing?.Set());

        // Changes still on their way to the server go first, within a time limit, and any left are noted in the log.
        Step(() =>
        {
            if (FinishPageWork?.Invoke(PageWorkLimit) is { Count: > 0 } left)
            {
                LogText($"Closed with {left.Count} {(left.Count == 1 ? "change" : "changes")} still on the way to the server: {string.Join(", ", left)}");
            }
        });

        Step(() => SaveDrafts?.Invoke());
        Step(() => SavePlace?.Invoke());
        Step(() => FinishNoteSync?.Invoke());
        Step(() => FlushSavedReads?.Invoke());
        Step(() => FinishRecording?.Invoke());

        // A capture or press read just before closing is still saved, within a few seconds. Off this thread, so
        // nothing it finishes can wait on the window thread being free.
        if (Worker is { } worker)
        {
            Step(() => System.Threading.Tasks.Task.Run(() => worker.StopDiscordAsync(DrainLimit)).Wait());
        }

        // What those events handed to this thread runs now, since the dispatcher stops once closing is done. A typed
        // capture is one: it's marked as read in Discord before it's handed over, so skipping it would lose it. Files
        // sent with it or for notes need Discord to download them, which has stopped by now. The rest only refreshes
        // the window or reads Discord again.
        Step(() => RunPosted?.Invoke());

        // Work started without being waited for, such as a notice going out or a round of reminder checks, is given a
        // few seconds to end. Nothing new is started meanwhile: no timer runs, and the tray's menu is off, so no
        // dialog opens from it while this waits.
        Step(() => StopIntake?.Invoke());
        Step(() => HideTray?.Invoke());
        Step(() =>
        {
            if (Worker is { } stopping)
            {
                WaitWhileRunning(dispatcher, stopping.StopWorkAsync(DrainLimit));
            }
        });

        // Outside a night's check-in window, nothing about the last check-in is left saved.
        Step(() => Worker?.ForgetLateNight());
        Step(() => DisposeTranscription?.Invoke());
        Step(() => Release?.Invoke());
    }

    /// <summary>
    /// Waits here for <paramref name="work"/> while the window thread keeps running, since work started from it
    /// carries on there and would otherwise never get the chance to end.
    /// </summary>
    public static void WaitWhileRunning(Dispatcher dispatcher, System.Threading.Tasks.Task work)
    {
        var frame = new DispatcherFrame();
        work.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false), System.Threading.Tasks.TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
    }
}
