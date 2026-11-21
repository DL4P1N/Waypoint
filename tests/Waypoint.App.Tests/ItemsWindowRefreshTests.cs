using System.Windows;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>A hidden window's page isn't refreshed, since nothing's on screen, and it catches up once shown again.</summary>
public sealed class ItemsWindowRefreshTests
{
    [Fact]
    public void A_hidden_window_skips_its_page_refreshes_and_refreshes_once_shown_again() => Ui.Run(ui =>
    {
        ui.Database.SetPreference("last_page", "History");
        var history = new CountingHistory(new LocalHistoryData(ui.Database));
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { }, history: history)
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
        };
        try
        {
            window.ShowLastPage();
            ServerToDoClickTests.PumpUntil(ui, () => history.Searches > 0, "the first refresh");
            window.Refresh(0, null, null, null, null, null);
            ServerToDoClickTests.PumpUntil(ui, () => history.Searches > 1, "a refresh while shown");

            window.Hide();
            var before = history.Searches;
            for (var round = 0; round < 3; round++)
            {
                window.Refresh(0, null, null, null, null, null);
                ui.Pump();
            }

            Assert.Equal(before, history.Searches);

            window.ShowLastPage();
            ServerToDoClickTests.PumpUntil(ui, () => history.Searches > before, "the refresh on showing");
            ui.Pump();
            Assert.Equal(before + 1, history.Searches);
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class CountingHistory(IHistoryData inner) : IHistoryData
    {
        private int _searches;

        public int Searches => Volatile.Read(ref _searches);

        public string? GetPreference(string key) => inner.GetPreference(key);

        public void SetPreference(string key, string value) => inner.SetPreference(key, value);

        public IReadOnlyList<HistoryRow> SearchHistory(string? words, int limit, HistoryFilter filter)
        {
            Interlocked.Increment(ref _searches);
            return inner.SearchHistory(words, limit, filter);
        }

        public DeletedItem? DeleteHistoryItem(HistoryEntry entry, DateTime now) => inner.DeleteHistoryItem(entry, now);

        public void UndoDelete(DeletedItem deleted, DateTime now) => inner.UndoDelete(deleted, now);

        public bool ReopenEntry(HistoryEntry entry) => inner.ReopenEntry(entry);
    }
}
