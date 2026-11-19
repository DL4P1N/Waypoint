using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>
/// Find anywhere: one box that looks through To do, Notes and their lists, History, and Budget at once, with no Claude call.
/// Picking a result shows it on its page. It reads through the same data the pages use, so it works in server mode.
/// </summary>
public partial class FindView : UserControl
{
    public const string StartHint = "Type to look through To do, Notes, lists, History, and Budget at once.";

    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private FindSources? _sources;

    public FindView()
    {
        InitializeComponent();
        Hint.Text = StartHint;
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            SearchSoon();
        };
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
        Show(text, _sources is null ? [] : FindAnywhere.Search(text, _sources));
    }

    /// <summary>
    /// Once typing pauses. In server mode each page is asked over the network, so the search is made off the window's
    /// thread and typing never waits on the server. Results for text that's been typed over since are dropped.
    /// </summary>
    private void SearchSoon()
    {
        if (_sources is not { ToDo: ServerToDoData } sources)
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
        var found = await System.Threading.Tasks.Task.Run(() => FindAnywhere.Search(text, sources));
        if (asked != _searches || SearchBox.Text.Trim() != text)
        {
            return false;
        }

        Show(text, found);
        return true;
    }

    /// <summary>
    /// Searches now, unless in server mode the results shown are already for what's in the box, then carries on. In
    /// server mode a search still to make carries on once its results are shown, and not at all if more was typed.
    /// </summary>
    private void SearchThen(Action then)
    {
        if (_sources is { ToDo: ServerToDoData } sources)
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

    /// <summary>What Enter or Down is waiting on in server mode, so tests can tell when it's done.</summary>
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
    public void Leave() => _searches++;

    private void Close()
    {
        Leave();
        Closed?.Invoke();
    }

    private void Show(string text, System.Collections.Generic.IReadOnlyList<FoundThing> found)
    {
        _shownFor = text;
        Results.ItemsSource = found;
        if (found.Count > 0)
        {
            Results.SelectedIndex = 0;
        }

        Hint.Text = text.Length < FindAnywhere.MinLength
            ? StartHint
            : found.Count == 0 ? $"Nothing on any page matches \"{text}\"." : "";
        Hint.Visibility = Hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
