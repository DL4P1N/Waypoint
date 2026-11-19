using System;
using System.Drawing;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core.Background;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>The tray icon and its menu, and the recording item on it.</summary>
public partial class App
{
    private void ShowRecordingState()
    {
        if (_recordItem is not null)
        {
            _recordItem.Text = _recorder?.IsRecording == true ? "Stop recording" : "Start recording";
        }

        RefreshStatus();
    }

    private void OnRecordItemClicked()
    {
        if (_recorder?.IsRecording == true)
        {
            _recorder.Stop();
        }
        else
        {
            _recorder?.AskToStart();
        }
    }

    /// <summary>The tray's tip when there's nothing else to say. A trial run's tip says so, from <see cref="TrialRun.TrayTip"/>.</summary>
    private const string TrayIdle = "Waypoint";

    private static string GetDataDirectory()
    {
        var directory = ChosenDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waypoint");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private Forms.NotifyIcon CreateTray()
    {
        var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows") { Checked = StartupSetting.IsEnabled, CheckOnClick = true };
        startWithWindows.Click += (_, _) => StartupSetting.Set(startWithWindows.Checked);

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new TrayMenuColors(_palette ?? Themes.Default)),
            BackColor = Color.Black,
            ForeColor = Color.White,
        };
        menu.HandleCreated += (_, _) => WindowStyling.Apply(menu.Handle);
        menu.Items.Add("Ask Waypoint...", null, (_, _) => _captureWindow.ShowForCapture());
        menu.Items.Add("Open Waypoint", null, (_, _) => _itemsWindow.ShowList());
        menu.Items.Add(NextStepQuestion, null, (_, _) => OnCaptureSubmitted(NextStepQuestion));
        // The same form as To do's Save my spot, so it works without sorting. Opened once the menu has closed, like Pair.
        menu.Items.Add("Save my spot...", null, (_, _) => Dispatcher.BeginInvoke(_itemsWindow.OpenSaveSpot));

        // Lines group the menu: what's used every day, recording, connecting money and the server, backups, and
        // starting with Windows, then Exit on its own.
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Recording stays on this PC in both modes. Its notes go to the server in server mode.
        _recordItem = new Forms.ToolStripMenuItem(_recorder?.IsRecording == true ? "Stop recording" : "Start recording");
        _recordItem.Click += (_, _) => OnRecordItemClicked();
        menu.Items.Add(_recordItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (_server is not null)
        {
            // The rest work on this PC's database, so in server mode they're left out until they go through the server.
            // Opened once the tray menu has closed, so the form isn't held inside the menu's own click and shows up
            // as a window of its own, which is how screen readers and UI Automation find it.
            menu.Items.Add("Pair with server...", null, (_, _) => Dispatcher.BeginInvoke(PairWithServer));
            menu.Items.Add("Import card or bank CSV...", null, (_, _) => ImportCsv());
            menu.Items.Add("Connect alert email", null, (_, _) => _ = ConnectServerEmailAsync(_server));
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddServerBackupItems(menu, _server);
        }
        else
        {
            menu.Items.Add("Import card or bank CSV...", null, (_, _) => ImportCsv());
            menu.Items.Add("Connect alert email", null, (_, _) => _ = ConnectEmailAsync());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Back up now", null, (_, _) => BackUpNow());
            menu.Items.Add("Restore a backup...", null, (_, _) => RestoreBackup());
            menu.Items.Add("Export notes...", null, (_, _) => ExportNotes());
        }

        // The sign-in entry belongs to the installed copy. From a copy on its own data folder, unchecking it would
        // remove the real one, and checking it would point it at this copy, which starts without its data folder.
        if (ChosenDataDirectory is null)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(startWithWindows);
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        using var iconStream = typeof(App).Assembly.GetManifestResourceStream("waypoint.ico")!;
        var icon = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);

        var tray = new Forms.NotifyIcon { Icon = icon, Text = Trial.TrayTip(TrayIdle, MaxTrayTextLength), Visible = true, ContextMenuStrip = menu };
        tray.BalloonTipClicked += (_, _) => _itemsWindow.ShowList();
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                _itemsWindow.ShowList();
            }
        };
        return tray;
    }
}
