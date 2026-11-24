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

    /// <summary>
    /// Set once a restore starts or Waypoint starts closing. Nothing new is taken from then on: the tray and a second
    /// launch no longer open the list, and a restore can't start.
    /// </summary>
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
            StopTimers();
            _askKey?.Dispose();
            _askKey = null;
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

        // Transcription goes before the worker's work, so work waiting on a transcription can end.
        limit => _transcription is { } transcription
            ? System.Threading.Tasks.Task.Run(() => transcription.Stop(limit))
            : System.Threading.Tasks.Task.FromResult(true),
        StopWorkerWorkAsync,
        limit => _itemsWindow.NotesPage.StopWritingAsync(limit),
    ];

    /// <summary>
    /// The work the worker started without waiting for it, such as a notice going out or the feed being read, stopped
    /// the same way before a restore and when closing. True once it has ended within <paramref name="limit"/>.
    /// </summary>
    private System.Threading.Tasks.Task<bool> StopWorkerWorkAsync(TimeSpan limit) =>
        _worker?.StopWorkAsync(limit) ?? System.Threading.Tasks.Task.FromResult(true);

    private void StopTimers()
    {
        foreach (var timer in new[] { _alertTimer, _calendarTimer, _discordTimer, _claimTimer, _emailTimer, _statusTimer, _serverTimer, _idleTimer })
        {
            timer?.Stop();
        }
    }

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

        RequestExit();
    }

    /// <summary>
    /// Closes the usual way, from the tray, from install.ps1 or another program asking, or after a restore. A file or
    /// folder dialog still open is closed first and closing goes ahead once it has returned, so it can't hold closing
    /// open and nothing picked in it is acted on after closing.
    /// </summary>
    private void RequestExit() => FileDialogs.Shared.CloseThen(() => Shutdown());

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
        // A second copy exits before any of this is set up, so each part is checked before it's used.
        Closing().Run(Dispatcher);
        base.OnExit(e);
    }

    /// <summary>What closing goes through, from what this copy set up.</summary>
    private AppClosing Closing() => new()
    {
        Closing = _closing,
        FinishPageWork = _itemsWindow is { } pages ? pages.FinishWork : null,
        PageWorkLimit = PageWorkLimit,
        SaveDrafts = _itemsWindow is { } drafts ? drafts.SaveDrafts : null,
        SavePlace = _itemsWindow is { } place ? place.SavePlace : null,
        FinishNoteSync = _noteSync is { } sync ? sync.Finish : null,
        FlushSavedReads = _server is { } server ? server.Client.FlushSavedReads : null,
        FinishRecording = _recorder is { } recorder ? recorder.Dispose : null,
        Worker = _worker,
        RunPosted = _posted is { } posted ? posted.RunLeft : null,
        StopIntake = () =>
        {
            _restoring = true;
            StopTimers();
        },
        HideTray = () =>
        {
            if (_tray is { } tray)
            {
                if (tray.ContextMenuStrip is { } menu)
                {
                    menu.Enabled = false;
                }

                tray.Visible = false;
            }
        },
        DisposeTranscription = _transcription is { } transcription ? transcription.Dispose : null,
        Release = () =>
        {
            try
            {
                CornerCards.Changed -= OnCornerCardsChanged;
                _cardKey?.Dispose();
                _cardKey = null;
                _tray?.Dispose();
            }
            finally
            {
                // Released, not just dropped, so a copy started after a restore takes it over cleanly.
                if (_holdsInstance)
                {
                    _singleInstance?.ReleaseMutex();
                }

                _singleInstance?.Dispose();
            }
        },
        LogError = LogError,
        LogText = LogText,
    };
}
