using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Remote;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>Syncing with the server: a round of sending and showing replies, notes kept while it was away, and pairing.</summary>
public partial class App
{
    /// <summary>One round with the server: check it, send what's waiting, show replies, and refresh the pages.</summary>
    private async System.Threading.Tasks.Task SyncWithServerAsync()
    {
        if (_server is null)
        {
            return;
        }

        try
        {
            foreach (var reply in await _server.SyncAsync())
            {
                ShowServerReply(reply);
            }

            // Discord settings saved while there was no address go to the server once it's reached.
            // While the server is away they wait for the next round.
            var server = _server;
            if (server.Client.IsOnline && _database.GetPreference(PcMode.HeldDiscordKey) == "1")
            {
                try
                {
                    var token = new ProtectedTokenStore(Path.Combine(GetDataDirectory(), DiscordSetup.TokenFile)).Load();
                    if (await System.Threading.Tasks.Task.Run(() => PcMode.SendHeldDiscord(_database, token, server.Settings.SaveDiscord)) is { } saved)
                    {
                        _itemsWindow.ShowReply($"Discord settings saved on this PC were sent to the server. {saved}");
                    }
                }
                catch (ServerUnavailableException)
                {
                }
            }

            // The server's classes and bedtime are the ones in use, read each round so a change saved from another
            // PC shows here without a restart. While it's away the last ones read are kept.
            var client = _server.Client;
            var (classes, bedtime) = await System.Threading.Tasks.Task.Run(() =>
                (Waypoint.Core.Remote.ServerSettingsFollow.FindClasses(client), Waypoint.Core.Remote.ServerSettingsFollow.FindBedtime(client)));
            if (Waypoint.Core.Remote.ServerSettingsFollow.Apply(classes, bedtime, _schedule, _bedtimeSchedule, _database))
            {
                _itemsWindow.ClassesChanged();
            }

            // Typing in notes while the server was away is saved now. A note changed there meanwhile keeps both.
            var sent = await _server.Notes.SendOfflineAsync();
            if (sent.Count > 0)
            {
                ShowNotesSent(sent);
            }

            // What the server couldn't get to Discord, such as "Time to leave", shows here the way it would locally.
            // A trial run leaves them for the installed copy, since each is shown only on the first PC to claim it.
            if (Trial.StartsAlerts)
            {
                foreach (var notice in await _server.Client.TakeNoticesAsync())
                {
                    ShowToast(notice.Title, notice.Text);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            LogError(exception);
        }

        await RefreshStatusAheadAsync();
    }

    /// <summary>
    /// The refresh made every so often. In server mode what the pages read is read first off this thread, and the
    /// pages are then shown from those reads, so a slow or unreachable server never holds the window still.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshStatusAheadAsync()
    {
        if (_server is not { } server)
        {
            RefreshStatus();
            return;
        }

        await server.Client.RefreshReadsAsync();
        using (server.Client.UseRecentReads())
        {
            RefreshStatus();
        }
    }

    private void ShowNotesSent(IReadOnlyList<Waypoint.Core.Remote.OfflineNoteSent> sent)
    {
        var both = sent.Count(note => note.KeptBoth);
        var suffix = Waypoint.Core.Remote.OfflineNotes.KeptBothSuffix.Trim();
        if (both == 1)
        {
            _itemsWindow.ShowReply($"A note changed on the server while it was away, so both versions are kept. What was typed here is the note ending in {suffix}.");
        }
        else if (both > 1)
        {
            _itemsWindow.ShowReply($"{both} notes changed on the server while it was away, so both versions are kept. What was typed here is in the notes ending in {suffix}.");
        }

        // Turned down for good, such as a note too big for the server. The text is kept in a file on this PC.
        if (sent.FirstOrDefault(note => note.Problem is not null) is { } refused)
        {
            var count = sent.Count(note => note.Problem is not null);
            var what = count == 1 ? "A note typed while the server was away couldn't be saved there" : $"{count} notes typed while the server was away couldn't be saved there";
            _itemsWindow.ShowReply($"{what}. {refused.Problem} Nothing is lost: {(count == 1 ? "it's" : "they're")} kept on this PC.", "Open", () => OpenKept(() => _server?.Notes.WriteUnsavedCopy()));
        }

        _itemsWindow.NotesPage.FollowSent(sent);
        _itemsWindow.NotesPage.Refresh(force: true);
    }

    /// <summary>
    /// Opens a plain text copy of what the server couldn't save, in the PC's text editor, so it can be copied back
    /// in. The kept file stays as it is, so opening it never loses anything.
    /// </summary>
    /// <returns>Whether it opened.</returns>
    private bool OpenKept(Func<string?> write)
    {
        try
        {
            if (write() is { } copy)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(copy) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _itemsWindow.ShowReply($"Couldn't open what's kept on this PC. {exception.Message}");
            _itemsWindow.ShowList();
        }

        return false;
    }

    /// <summary>
    /// Files found damaged and set aside are told about on the status line, after a restart too, until they're opened
    /// or the notice is dismissed. Each file is remembered by name, so one set aside later is told about again.
    /// </summary>
    private void SetAsideSeen(ServerConnection server)
    {
        try
        {
            server.Queue.MarkSetAsideSeen();
            server.Notes.MarkSetAsideSeen();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not remembered, so the notice comes back next time, which loses nothing.
            LogError(exception);
        }

        RefreshStatus();
    }

    /// <summary>The same as a local reply, with notifications assumed to go out from the server through Discord.</summary>
    private void ShowServerReply(ServerReply reply)
    {
        _itemsWindow.ShowReply(reply.Text, reply.Made);
        if (!_itemsWindow.IsVisible && (reply.StartedSession || reply.OpensWindow))
        {
            _itemsWindow.ShowList();
        }
    }

    private void PairWithServer()
    {
        _itemsWindow.ShowList();
        var form = RenameWindow.ForPairing(_itemsWindow);
        if (form.ShowDialog() != true || string.IsNullOrWhiteSpace(form.NewName))
        {
            return;
        }

        _server!.Pair(form.NewName);
        _itemsWindow.ShowReply("Paired. Checking the server now.");
        _ = SyncWithServerAsync();
    }
}
