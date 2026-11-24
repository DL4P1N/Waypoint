using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>
/// Find anywhere: one box that looks through To do, Notes and their lists, History, and Budget at once, with no Claude call.
/// Picking a result shows it on its page. It reads through the same data the pages use, so it works in server mode.
/// </summary>
public partial class FindView : UserControl
{
    public const string StartHint = "Type to look through To do, Notes, lists, History, and Budget at once.";

    public const string SearchingHint = "Searching...";

    public const string PartialHint = "Some places couldn't be searched just now.";

    public const string NoneReachedHint = "Nothing could be searched just now, because the server couldn't be reached. Try again in a moment.";

    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(200) };

    // Searching... only shows for a search that takes a moment, so a quick one doesn't flicker.
    private readonly DispatcherTimer _searching = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private FindSources? _sources;

    // What the hint says for the results shown, to go back to if a search is dropped.
    private string _hint = StartHint;

    public FindView()
    {
        InitializeComponent();
        Hint.Text = StartHint;
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            SearchSoon();
        };
        _searching.Tick += (_, _) => OnSearchingTick();
    }

    /// <summary>A result was picked, to be shown on its page.</summary>
    public event Action<FoundThing>? Chosen;

    /// <summary>Esc or Close, to go back to the page that was open.</summary>
    public event Action? Closed;

    public void Connect(FindSources sources) => _sources = sources;

    /// <summary>Puts the cursor in the box with the last search selected, so typing starts a new one.</summary>
    public void FocusSearch()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    // Counts each search, so one that comes back after a newer one started is dropped.
    private int _searches;

    // What the results shown were found for, so Enter doesn't ask the server again for the same text.
    private string? _shownFor;

    /// <summary>Searches for what's in the box now, without waiting for typing to pause. In server mode the results come once the server answers.</summary>
    public void Search()
    {
        _typing.Stop();
        if (_sources is { ToDo: ServerToDoData } sources)
        {
            SearchOffThread(sources);
            return;
        }

        _searches++;
        var text = SearchBox.Text.Trim();
        Show(text, _sources is null ? Nothing : FindAnywhere.Look(text, _sources));
    }

    /// <summary>
    /// Once typing pauses. The search is made off the window's thread, since every page is read for it, or asked over
    /// the network in server mode, so typing never waits on it. Results for text that's been typed over since are dropped.
    /// </summary>
    private void SearchSoon()
    {
        if (_sources is not { } sources)
        {
            Search();
            return;
        }

        SearchOffThread(sources);
    }

    private async void SearchOffThread(FindSources sources) => await SearchOffThreadAsync(sources);

    /// <summary>True once the results are shown, and false when they were dropped for text typed since.</summary>
    private async System.Threading.Tasks.Task<bool> SearchOffThreadAsync(FindSources sources)
    {
        var asked = ++_searches;
        var text = SearchBox.Text.Trim();
        _searching.Stop();
        _searching.Start();
        FindResult found;
        try
        {
            found = await System.Threading.Tasks.Task.Run(() => FindAnywhere.Look(text, sources));
        }
        finally
        {
            if (asked == _searches)
            {
                _searching.Stop();
            }
        }

        if (asked != _searches || SearchBox.Text.Trim() != text)
        {
            if (asked == _searches)
            {
                ShowHint(_hint);
            }

            return false;
        }

        Show(text, found);
        return true;
    }

    private static FindResult Nothing => new([], [], []);

    /// <summary>A search is still on its way after a moment, so the hint says so. The results shown stay until it answers.</summary>
    private void OnSearchingTick()
    {
        _searching.Stop();
        ShowHint(SearchingHint);
    }

    /// <summary>
    /// Searches now, unless the results shown are already for what's in the box, then carries on. A search still to
    /// make is made off the window's thread and carries on once its results are shown, and not at all if more was typed.
    /// </summary>
    private void SearchThen(Action then)
    {
        if (_sources is { } sources)
        {
            if (_shownFor != SearchBox.Text.Trim())
            {
                _typing.Stop();
                Waiting = ThenLater(SearchOffThreadAsync(sources), then);
                return;
            }
        }
        else
        {
            Search();
        }

        then();
    }

    /// <summary>What Enter or Down is waiting on when a search was still to make, so tests can tell when it's done.</summary>
    internal System.Threading.Tasks.Task Waiting { get; private set; } = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>Carries on once the search is shown, but not if Find was left or the box changed meanwhile.</summary>
    private async System.Threading.Tasks.Task ThenLater(System.Threading.Tasks.Task<bool> searching, Action then)
    {
        var asked = _searches;
        var text = SearchBox.Text.Trim();
        if (await searching && asked == _searches && IsVisible && SearchBox.Text.Trim() == text)
        {
            then();
        }
    }

    /// <summary>Leaving Find drops a search still on its way, so its results don't pull the user anywhere.</summary>
    public void Leave()
    {
        _searches++;
        _searching.Stop();
        ShowHint(_hint);
    }

    private void Close()
    {
        Leave();
        Closed?.Invoke();
    }

    /// <summary>
    /// Shows the results. "Nothing matches" is only said when every page answered. When some couldn't be reached
    /// the results that came show with a quiet note, and when none could, the hint says so instead.
    /// </summary>
    private void Show(string text, FindResult result)
    {
        var found = result.Found;
        _shownFor = text;
        Results.ItemsSource = found;
        if (found.Count > 0)
        {
            Results.SelectedIndex = 0;
        }

        _hint = text.Length < FindAnywhere.MinLength ? StartHint
            : result.Partial && result.Answered.Count == 0 ? NoneReachedHint
            : result.Partial ? PartialHint
            : found.Count == 0 ? CardLabels.NothingMatches("on any page", text)
            : "";
        ShowHint(_hint);
    }

    private void ShowHint(string hint)
    {
        Hint.Text = hint;
        Hint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _typing.Stop();
        _typing.Start();
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                SearchThen(() => Choose(Results.SelectedItem as FoundThing));
                break;
            case Key.Down when Results.Items.Count > 0:
                e.Handled = true;
                SearchThen(() =>
                {
                    Results.SelectedIndex = Math.Min(1, Results.Items.Count - 1);
                    FocusSelected();
                });
                break;
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
        }
    }

    private void OnResultsKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Choose(Results.SelectedItem as FoundThing);
                break;
            case Key.Up when Results.SelectedIndex <= 0:
                e.Handled = true;
                SearchBox.Focus();
                break;
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
        }
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Results, source) is ListBoxItem { DataContext: FoundThing thing })
        {
            Choose(thing);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void FocusSelected()
    {
        Results.UpdateLayout();
        (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) as ListBoxItem)?.Focus();
    }

    private void Choose(FoundThing? thing)
    {
        if (thing is not null)
        {
            Chosen?.Invoke(thing);
        }
    }
}
