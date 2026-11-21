using System;
using System.Drawing;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>Closing the app: waiting for changes on their way to the server, then stopping everything.</summary>
public partial class App
{
    /// <summary>How long closing waits for changes still on their way to the server, a few slow answers' worth.</summary>
    private static readonly TimeSpan PageWorkLimit = TimeSpan.FromSeconds(10);

    /// <summary>Starts the copy that comes after a restore. The words after it are what to say once it's open.</summary>
    private const string AfterRestoreArgument = "--after-restore";

    /// <summary>How long that copy waits for this one to finish closing before it gives up.</summary>
    private static readonly TimeSpan AfterRestoreWait = TimeSpan.FromMinutes(2);

    /// <summary>Set once a restore starts. Nothing new is taken from then on, and Waypoint starts again after.</summary>
    private bool _restoring;

    /// <summary>Whether this copy holds the single-instance lock, so it lets go of it properly on the way out.</summary>
    private bool _holdsInstance;

    /// <summary>
    /// What closing stops, stopped the same way before a restore so nothing started against the old data finishes
    /// against the restored copy. Nothing new is taken first: the windows go, the tray menu is off, and the timers that
    /// check reminders, Discord, email, the calendar, and the server stop. Each gives whether it stopped in time.
    /// </summary>
    private List<Func<TimeSpan, System.Threading.Tasks.Task<bool>>> RestoreStops() =>
    [
        _ =>
        {
            foreach (Window window in Windows)
            {
                window.Hide();
            }

            if (_tray?.ContextMenuStrip is { } menu)
            {
                menu.Enabled = false;
            }

            _later.Clear();
            foreach (var timer in new[] { _alertTimer, _calendarTimer, _discordTimer, _claimTimer, _emailTimer, _statusTimer, _serverTimer, _idleTimer })
            {
                timer?.Stop();
            }

            return System.Threading.Tasks.Task.FromResult(true);
        },

        // Discord events already read are finished, then what they handed to this thread, as when closing.
        async limit =>
        {
            if (_worker is { } worker)
            {
                await System.Threading.Tasks.Task.Run(() => worker.StopDiscordAsync(DiscordGateway.EventDrainLimit));
            }

            _posted?.RunLeft();
            return true;
        },
        limit => _captures.StopAsync(limit),
        limit => _itemsWindow.NotesPage.StopWritingAsync(limit),
        limit => _transcription is { } transcription
            ? System.Threading.Tasks.Task.Run(() => transcription.Stop(limit))
            : System.Threading.Tasks.Task.FromResult(true),
    ];

    /// <summary>
    /// Starts the next copy, then closes the usual way. Used after a restore, whether or not it went in, since what was
    /// stopped for it isn't started again in this run. The next copy waits for this one to let go of the lock, then says
    /// <paramref name="said"/>. If it can't be started, carrying on here isn't safe either, so this says to open
    /// Waypoint again before closing, rather than closing without a word.
    /// </summary>
    private void RestartWith(string said)
    {
        if (!StartAfterRestore(said))
        {
            NoteConfirmWindow.Tell(null, RestoreTitle, $"{said} Waypoint couldn't open again by itself, so it closes now. Open it again the way you usually do.");
        }

        Shutdown();
    }

    private static bool StartAfterRestore(string said)
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return false;
        }

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(AfterRestoreArgument);
            start.ArgumentList.Add(said);
            return System.Diagnostics.Process.Start(start) is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LogError(exception);
            return false;
        }
    }

    /// <summary>
    /// For the copy started after a restore: waits for the one before to finish closing and let go of the lock, so it
    /// isn't simply asked to open its list. True once this copy holds it.
    /// </summary>
    private static bool WaitForEarlierCopy(Mutex singleInstance)
    {
        try
        {
            return singleInstance.WaitOne(AfterRestoreWait);
        }
        catch (AbandonedMutexException)
        {
            // Let go of without being released, as a closing copy does. It's held by this one now.
            return true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // A second copy exits before any of this is set up, so each part is checked before it's used. Changes still on
        // their way to the server go first, within a time limit, and any left are noted in the log.
        // Each part is done on its own, so a problem with one is logged and the rest still happen.
        static void Step(Action step)
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

        // Said first, so install.ps1 waits for the rest rather than stopping this copy partway through saving.
        Step(() => _closing?.Set());

        Step(() =>
        {
            if (_itemsWindow?.FinishWork(PageWorkLimit) is { Count: > 0 } left)
            {
                LogText($"Closed with {left.Count} {(left.Count == 1 ? "change" : "changes")} still on the way to the server: {string.Join(", ", left)}");
            }
        });

        Step(() => _itemsWindow?.SaveDrafts());
        Step(() => _itemsWindow?.SavePlace());
        Step(() => _noteSync?.Finish());

        // The pages' saved view waits a moment after each change before it's written, so what's left is written now.
        Step(() => _server?.Client.FlushSavedReads());

        // Finishes the file, so what was recorded is offered as notes next start.
        Step(() => _recorder?.Dispose());

        // A capture or press read just before closing is still saved, within a few seconds. Off this thread, so
        // nothing it finishes can wait on the window thread being free.
        if (_worker is { } worker)
        {
            Step(() => System.Threading.Tasks.Task.Run(() => worker.StopDiscordAsync(DiscordGateway.EventDrainLimit)).Wait());
        }

        // What those events handed to this thread runs now, since the dispatcher stops once closing is done. A typed
        // capture is one: it's marked as read in Discord before it's handed over, so skipping it would lose it. Files
        // sent with it or for notes need Discord to download them, which has stopped by now. The rest only refreshes
        // the window or reads Discord again.
        Step(() => _posted?.RunLeft());

        // Outside a night's check-in window, nothing about the last check-in is left saved.
        Step(() => _worker?.ForgetLateNight());
        Step(() => _transcription?.Dispose());
        Step(() => _tray?.Dispose());
        Step(() =>
        {
            // Released, not just dropped, so a copy started after a restore takes it over cleanly.
            if (_holdsInstance)
            {
                _singleInstance?.ReleaseMutex();
            }

            _singleInstance?.Dispose();
        });
        base.OnExit(e);
    }
}
