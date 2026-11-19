using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Remote;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>Backing up and restoring on this PC, exporting notes, and importing bank files.</summary>
public partial class App
{
    private void BackUpNow()
    {
        try
        {
            _itemsWindow.SaveDrafts();
            var path = _database.BackUpNow(DateTime.Now);
            ShowToast("Backed up", $"Saved a copy in {Path.GetDirectoryName(path)}");
        }
        catch (Exception exception)
        {
            ShowToast("Backup didn't work", $"{PlainProblem.Report(exception)} Try Back up now again in a moment.");
        }
    }

    /// <summary>Asks first, and saves how things are now before replacing anything.</summary>
    private void RestoreBackup()
    {
        Directory.CreateDirectory(_database.BackupFolder);
        using var dialog = new Forms.OpenFileDialog
        {
            Filter = "Waypoint backups (*.db)|*.db",
            Title = "Choose a backup to restore",
            InitialDirectory = _database.BackupFolder,
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var when = File.GetLastWriteTime(dialog.FileName).ToString("dddd, MMM d 'at' h:mm tt", CultureInfo.CurrentCulture);
        if (!NoteConfirmWindow.Ask(
            _itemsWindow,
            RestoreTitle,
            $"Replace everything in Waypoint with the backup from {when}?",
            "A copy of how things are right now is saved in the backups folder first, so you can switch back.",
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
            var path = dialog.FileName;
            var result = _noteSync is { } sync
                ? sync.WhileHeld(() => _database.RestoreBackup(path, DateTime.Now))
                : _database.RestoreBackup(path, DateTime.Now);
            if (!result.Restored)
            {
                NoteConfirmWindow.Tell(null, RestoreTitle, result.Problem ?? "Nothing was changed.");
                return;
            }

            // Drafts were saved above, into the copy taken before the restore. What the pages still hold is from
            // before it, so it must not be written into the restored data on the way out.
            _itemsWindow.DiscardDrafts();

            if (NoteConfirmWindow.Ask(
                null,
                RestoreTitle,
                "Restored. Close Waypoint now? Then open it again the way you usually do.",
                $"Waypoint needs to close and open again to show it everywhere. How things were before is saved as {Path.GetFileName(result.SafetyCopy)}.",
                "Close Waypoint",
                "Not now"))
            {
                Shutdown();
            }
        }
        catch (Exception exception)
        {
            NoteConfirmWindow.Tell(null, RestoreTitle, $"The restore didn't work, so nothing was changed. {PlainProblem.Report(exception)} Try again, or pick another backup.");
        }
    }

    /// <summary>Into a new dated folder, so an earlier export is never written over.</summary>
    private void ExportNotes()
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
            var destination = Path.Combine(dialog.SelectedPath, name);
            var count = _database.ExportNotes(destination);
            ShowToast("Notes exported", $"Saved {count} {(count == 1 ? "note" : "notes")} in {name}");
        }
        catch (Exception exception)
        {
            ShowToast("Export didn't work", $"{PlainProblem.Report(exception)} Try again, or pick another folder.");
        }
    }

    private void ImportCsv()
    {
        using var dialog = new Forms.OpenFileDialog { Filter = "CSV files (*.csv)|*.csv", Title = "Import card or bank CSV", Multiselect = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        ImportStatements(dialog.FileNames.Select(File.ReadAllText));
    }

    /// <summary>
    /// In server mode the files are read here and imported there, so the server's budget is the one that changes.
    /// That can take the server a while, so the window isn't held up waiting.
    /// </summary>
    private async void ImportStatements(IEnumerable<string> statements)
    {
        if (_server is null)
        {
            ShowImported(_worker.ImportStatementsReply(statements));
            return;
        }

        var server = _server;
        var read = statements.ToList();
        try
        {
            ShowImported(await System.Threading.Tasks.Task.Run(() => server.Budget.ImportStatements(read)));
        }
        catch (ServerUnavailableException)
        {
            _itemsWindow.ShowReply("Can't reach the server, so nothing was imported. Import the file again once it's back.");
        }
        catch (ServerTimedOutException timedOut)
        {
            _itemsWindow.ShowReply(timedOut.Message);
        }
        catch (InvalidOperationException exception)
        {
            _itemsWindow.ShowReply($"The server couldn't import that. {PlainProblem.Report(exception)} Check that it's a card or checking export, then import it again.");
        }
    }

    /// <summary>What an import said, then Purchases, when it added any, so they're seen where they went.</summary>
    private void ShowImported(string reply)
    {
        _itemsWindow.ShowReply(reply);
        if (reply.StartsWith("Added ", StringComparison.Ordinal))
        {
            _itemsWindow.ShowPurchases();
        }
    }
}
