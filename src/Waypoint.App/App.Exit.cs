using System;
using System.Drawing;
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
        Step(() => _singleInstance?.Dispose());
        base.OnExit(e);
    }
}
