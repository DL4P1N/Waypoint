using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>What's typed, dropped, and replied: showing a capture's outcome, toasts, dropped files, and submitting a capture.</summary>
public partial class App
{
    /// <summary>Replies where the capture was typed, so the same reply never shows up in both places.</summary>
    private void ShowOutcome(CaptureOutcome outcome)
    {
        var reply = string.Join("\n", new[] { ItemGroups.OutcomeSummary(outcome), outcome.Answer }.Where(text => !string.IsNullOrWhiteSpace(text)));

        if (outcome.Origin == CaptureOrigin.Discord)
        {
            _worker.ReplyInDiscord(outcome, reply);
            return;
        }

        var made = outcome.CaptureId is { } capture ? _database.GetItemsFromCapture(capture) : [];
        _itemsWindow.ShowReply(reply, made);
        if (_itemsWindow.IsVisible)
        {
            return;
        }

        // The buttons live on the session card, so a new session opens the window.
        if (outcome.Activation is not null)
        {
            _itemsWindow.ShowList();
            return;
        }

        // App prompts don't reply in Discord, so an answer opens the window instead of a toast.
        if (_worker.Discord is null)
        {
            ShowToast("Waypoint", reply);
        }
        else if (!string.IsNullOrWhiteSpace(outcome.Answer) || outcome.Changes.Any(change => change.Applied && change.Change.Action == ChangeAction.BreakDown))
        {
            _itemsWindow.ShowList();
        }
    }

    /// <summary>The worker's fallbacks shown on this PC: a tray toast, a reminder card, and the status refresh.</summary>
    private sealed class WindowHost(App app, PostedWork posted) : IWorkerHost
    {
        public void ShowNotice(string title, string text) => OnUiThread(() => app.ShowToast(title, text));

        public void ShowReminder(DueReminder reminder, Action closed) => OnUiThread(() => app.ShowReminderCard(reminder, closed));

        public void ShowWeeklyReset(string text) => OnUiThread(() => app._itemsWindow.ShowReply(text));

        public void Changed() => OnUiThread(app.RefreshStatus);

        public void Post(Action action) => posted.Post(action);

        private void OnUiThread(Action action)
        {
            if (app.Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                app.Dispatcher.BeginInvoke(action);
            }
        }
    }

    private void ShowToast(string title, string text) =>
        _tray?.ShowBalloonTip(10000, title, text.Length > MaxBalloonTextLength ? text[..MaxBalloonTextLength] : text, Forms.ToolTipIcon.None);

    /// <summary>Files are only ever read, one at a time in the background. A bank export goes to the import.</summary>
    private void OnFilesDropped(string[] paths)
    {
        // Recordings become notes, so they go to the Notes page. Everything else is read here as before.
        var recordings = paths.Where(Waypoint.Core.Transcription.TranscriptionQueue.IsAudioFile).ToList();
        if (recordings.Count > 0)
        {
            _itemsWindow.ShowNotes();
            _itemsWindow.NotesPage.ImportAudio(recordings);
            paths = paths.Except(recordings).ToArray();
            if (paths.Length == 0)
            {
                return;
            }
        }

        _itemsWindow.ShowList();
        foreach (var path in paths)
        {
            _itemsWindow.ShowPrompt(Path.GetFileName(path.TrimEnd('\\', '/')));
            _ = ReadDroppedAsync(path);
        }
    }

    private async System.Threading.Tasks.Task ReadDroppedAsync(string path)
    {
        var statement = await System.Threading.Tasks.Task.Run(() => WaypointWorker.ReadStatement(path));
        if (statement is not null)
        {
            ImportStatements([statement]);
            return;
        }

        var file = await System.Threading.Tasks.Task.Run(() => ImageText.ReadFile(path));
        if (file.Problem is { } problem)
        {
            _itemsWindow.ShowReply(problem);
            return;
        }

        // Read here, sorted there, the same as typing it.
        if (_server is not null)
        {
            if (file.Text is { } text)
            {
                _server.Capture(text, file.Name);
                _ = SyncWithServerAsync();
            }

            return;
        }

        _captures.CaptureFile(file);
        _ = _captures.ProcessPendingAsync();
    }

    private void OnCaptureSubmitted(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _itemsWindow.ShowPrompt(text);
        }

        if (_server is not null)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                _server.Capture(text.Trim());
                _ = SyncWithServerAsync();
            }

            // Only the waiting count changed here, so the pages are shown from what was read lately.
            using (_server.Client.UseRecentReads())
            {
                RefreshStatus();
            }

            return;
        }

        if (_heldCaptures is { } held)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                held.Add(text.Trim(), DateTimeOffset.Now);
                _itemsWindow.ShowReply("Kept on this PC. It goes to the server once the server address is set in settings.json.");
            }

            RefreshStatus();
            return;
        }

        _captures.Capture(text);
        _ = _captures.ProcessPendingAsync();
    }
}
