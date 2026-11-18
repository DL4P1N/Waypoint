using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using Waypoint.Core.Capture;

namespace Waypoint.App;

/// <summary>
/// The Waiting on form: who, what about, when to check back, and their email if known. Check back starts on
/// the usual few days, so only Who needs typing. It hands the form to the page, which saves it with no sorter.
/// </summary>
public partial class WaitingWindow : Window
{
    private readonly Func<WaitingWindow, Task<string?>> _save;

    /// <param name="itemTitle">The task being marked, or null for a new wait, which then asks what it's about.</param>
    /// <param name="save">Saves the form, returning why it couldn't, or null once it's saved.</param>
    public WaitingWindow(Window owner, DateOnly today, string? itemTitle, Func<WaitingWindow, string?> save)
        : this(owner, today, itemTitle, form => Task.FromResult(save(form)))
    {
    }

    /// <summary>The same form with a save that may be on its way to the server for a moment. See <see cref="FormSaving"/>.</summary>
    public static WaitingWindow Saving(Window owner, DateOnly today, string? itemTitle, Func<WaitingWindow, Task<string?>> save) =>
        new(owner, today, itemTitle, save);

    private WaitingWindow(Window owner, DateOnly today, string? itemTitle, Func<WaitingWindow, Task<string?>> save)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        _save = save;

        if (itemTitle is not null)
        {
            ItemTitle.Text = itemTitle;
            ItemTitle.Visibility = Visibility.Visible;
            TopicPanel.Visibility = Visibility.Collapsed;
        }

        var usual = WaitingOn.DefaultFollowUp(today);
        FollowUps.ItemsSource = new[]
        {
            new Choice($"In a few days, {Day(usual)}", usual),
            new Choice("Tomorrow", today.AddDays(1)),
            new Choice($"Next week, {Day(today.AddDays(7))}", today.AddDays(7)),
            new Choice($"In two weeks, {Day(today.AddDays(14))}", today.AddDays(14)),
        };
        FollowUps.SelectedIndex = 0;
        Loaded += (_, _) => WhoBox.Focus();
    }

    public string Who => WhoBox.Text.Trim();

    public string? Topic => string.IsNullOrWhiteSpace(TopicBox.Text) ? null : TopicBox.Text.Trim();

    public string? Email => string.IsNullOrWhiteSpace(EmailBox.Text) ? null : EmailBox.Text.Trim();

    public DateOnly? FollowUp => (FollowUps.SelectedItem as Choice)?.Value;

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var problem = Who.Length == 0 ? "Say who you're waiting on."
            : Email is { } email && !email.Contains('@') ? "That email doesn't look right. Leave it empty if you're not sure."
            : null;
        if (problem is not null)
        {
            ShowProblem(problem);
            return;
        }

        FormSaving.Then(this, SaveButton, _save(this), saved =>
        {
            if (saved is not null)
            {
                ShowProblem(saved);
                return;
            }

            DialogResult = true;
        });
    }

    private void ShowProblem(string problem)
    {
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
        LiveRegion.Announce(Problem);
    }

    private static string Day(DateOnly day) => day.ToString("ddd, MMM d", CultureInfo.CurrentCulture);

    private sealed record Choice(string Label, DateOnly Value)
    {
        /// <summary>What a screen reader says for the choice, rather than the record's fields.</summary>
        public override string ToString() => Label;
    }
}
