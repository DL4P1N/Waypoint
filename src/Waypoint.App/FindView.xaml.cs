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

    /// <summary>Searches for what's in the box now, without waiting for typing to pause.</summary>
    public void Search()
    {
        _typing.Stop();
        _searches++;
        var text = SearchBox.Text.Trim();
        Show(text, _sources is null ? [] : FindAnywhere.Search(text, _sources));
    }

    /// <summary>
    /// Once typing pauses. In server mode each page is asked over the network, so the search is made off the window's
    /// thread and typing never waits on the server. Results for text that's been typed over since are dropped.
    /// </summary>
    private async void SearchSoon()
    {
        if (_sources is not { ToDo: ServerToDoData } sources)
        {
            Search();
            return;
        }

        var asked = ++_searches;
        var text = SearchBox.Text.Trim();
        var found = await System.Threading.Tasks.Task.Run(() => FindAnywhere.Search(text, sources));
        if (asked == _searches && SearchBox.Text.Trim() == text)
        {
            Show(text, found);
        }
    }

    /// <summary>Searches now, unless in server mode the results shown are already for what's in the box.</summary>
    private void SearchUnlessShown()
    {
        if (_sources is not { ToDo: ServerToDoData } || _shownFor != SearchBox.Text.Trim())
        {
            Search();
        }
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
                SearchUnlessShown();
                Choose(Results.SelectedItem as FoundThing);
                break;
            case Key.Down when Results.Items.Count > 0:
                e.Handled = true;
                SearchUnlessShown();
                Results.SelectedIndex = Math.Min(1, Results.Items.Count - 1);
                FocusSelected();
                break;
            case Key.Escape:
                e.Handled = true;
                Closed?.Invoke();
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
                Closed?.Invoke();
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

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Closed?.Invoke();

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
