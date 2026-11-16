using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Waypoint.Core;
using Waypoint.Core.Remote;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>
/// Backups, restores, and the notes export in server mode. They run on the server, where the database is, so the
/// copies are kept in the server's backups folder and a restore is picked from the ones it has.
/// </summary>
public partial class App
{
    private const string RestoreTitle = "Restore a backup";

    private void AddServerBackupItems(Forms.ContextMenuStrip menu, ServerConnection server)
    {
        menu.Items.Add("Back up now", null, (_, _) => BackUpOnServer(server));

        // The list is read from the server each time the menu opens, so it's never out of date.
        var restore = new Forms.ToolStripMenuItem("Restore a backup");
        restore.DropDownItems.Add(new Forms.ToolStripMenuItem("Checking the server...") { Enabled = false });
        restore.DropDownOpening += (_, _) => FillServerBackups(restore, server);
        menu.Items.Add(restore);

        menu.Items.Add("Export notes...", null, (_, _) => ExportNotesFromServer(server));
    }

    private async void BackUpOnServer(ServerConnection server)
    {
        try
        {
            _itemsWindow.SaveDrafts();
            var name = await Task.Run(server.Backups.BackUpNow);
            ShowToast("Backed up", $"Saved a copy on the server as {name}");
        }
        catch (ServerUnavailableException away)
        {
            ShowToast("Backup didn't work", PageMessages.CantRun(away));
        }
        catch (ServerTimedOutException timedOut)
        {
            ShowToast("Backup is taking a while", timedOut.Message);
        }
        catch (Exception exception)
        {
            ShowToast("Backup didn't work", $"{PlainProblem.Report(exception)} Try Back up now again in a moment.");
        }
    }

    private void FillServerBackups(Forms.ToolStripMenuItem restore, ServerConnection server)
    {
        restore.DropDownItems.Clear();
        try
        {
            var backups = server.Backups.GetBackups();
            if (backups.Count == 0)
            {
                restore.DropDownItems.Add(new Forms.ToolStripMenuItem("No backups on the server yet") { Enabled = false });
            }

            foreach (var backup in backups)
            {
                restore.DropDownItems.Add(BackupLabel(backup), null, (_, _) => RestoreOnServer(server, backup));
            }
        }
        catch (Exception exception) when (exception is ServerUnavailableException or InvalidOperationException)
        {
            var text = exception is ServerUnavailableException away ? PageMessages.CantRun(away) : $"Couldn't list the backups. {PlainProblem.Report(exception)}";
            restore.DropDownItems.Add(new Forms.ToolStripMenuItem(text) { Enabled = false });
        }
    }

    private static string BackupLabel(BackupChoice backup) =>
        backup.At.ToString("dddd, MMM d 'at' h:mm tt", CultureInfo.CurrentCulture) + (backup.BeforeRestore ? " (before a restore)" : "");

    /// <summary>
    /// Asks first, the same as on this PC. The server saves how things are now before replacing anything. That can
    /// take it a while, so the window isn't held up waiting.
    /// </summary>
    private async void RestoreOnServer(ServerConnection server, BackupChoice backup)
    {
        if (!NoteConfirmWindow.Ask(
            _itemsWindow,
            RestoreTitle,
            $"Replace everything in Waypoint with the backup from {BackupLabel(backup)}?",
            "A copy of how things are right now is saved in the server's backups folder first, so you can switch back.",
            "Restore",
            "Cancel"))
        {
            return;
        }

        try
        {
            _itemsWindow.SaveDrafts();
            _itemsWindow.Hide();
            // The notes folder sync waits, so it can't undo the restore with the files from before it.
            var result = _noteSync is { } sync
                ? await sync.WhileHeldAsync(() => Task.Run(() => server.Backups.Restore(backup)))
                : await Task.Run(() => server.Backups.Restore(backup));
            if (!result.Restored)
            {
                NoteConfirmWindow.Tell(null, RestoreTitle, result.Problem ?? "Nothing was changed.");
                return;
            }

            // What the pages still hold is from before the restore, so it must not be written into the restored data.
            _itemsWindow.DiscardDrafts();

            var restarting = result.Restarting ? " The server starts again to load it, which takes a few seconds." : "";
            if (NoteConfirmWindow.Ask(
                null,
                RestoreTitle,
                "Restored. Close Waypoint now? Then open it again the way you usually do.",
                $"How things were before is saved on the server as {result.SafetyCopy}.{restarting}",
                "Close Waypoint",
                "Not now"))
            {
                Shutdown();
            }
        }
        catch (ServerUnavailableException away)
        {
            NoteConfirmWindow.Tell(null, RestoreTitle, $"Nothing was changed. {PageMessages.CantRun(away)}");
        }
        catch (ServerTimedOutException timedOut)
        {
            // It may have been restored, so what the pages still hold mustn't be written into the restored data.
            _itemsWindow.DiscardDrafts();
            NoteConfirmWindow.Tell(null, RestoreTitle, timedOut.Message);
        }
        catch (Exception exception)
        {
            NoteConfirmWindow.Tell(null, RestoreTitle, $"The restore didn't work, so nothing was changed. {PlainProblem.Report(exception)} Try again, or pick another backup.");
        }
    }

    /// <summary>The server sends every note as a zip, unpacked here into a new dated folder, as on this PC.</summary>
    private void ExportNotesFromServer(ServerConnection server)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where to save your notes", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            _itemsWindow.SaveDrafts();
            var name = $"Waypoint notes {DateTime.Now:yyyy-MM-dd HHmm}";
            var count = server.Backups.ExportNotes(Path.Combine(dialog.SelectedPath, name));
            ShowToast("Notes exported", $"Saved {count} {(count == 1 ? "note" : "notes")} in {name}");
        }
        catch (ServerUnavailableException away)
        {
            ShowToast("Export didn't work", PageMessages.CantRun(away));
        }
        catch (Exception exception)
        {
            ShowToast("Export didn't work", $"{PlainProblem.Report(exception)} Try again, or pick another folder.");
        }
    }
}
