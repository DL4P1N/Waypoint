using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Waypoint.Core.Capture;

namespace Waypoint.App;

/// <summary>
/// The Save my spot form: project, where it stopped, the next step, links, and loose thoughts.
/// It hands them to the page, which saves them the same way typing "save my spot" does.
/// </summary>
public partial class SaveSpotWindow : Window
{
    private readonly Func<SaveSpotWindow, OpReply> _save;

    /// <param name="project">Filled in to start with, such as the task being worked on.</param>
    /// <param name="save">Saves the form and gives back the reply. A reply that asks something keeps the form open.</param>
    public SaveSpotWindow(Window owner, string? project, Func<SaveSpotWindow, OpReply> save)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        _save = save;
        ProjectBox.Text = project ?? "";
        Loaded += (_, _) => (ProjectBox.Text.Length > 0 ? StoppedBox : ProjectBox).Focus();
    }

    public string Project => ProjectBox.Text;

    public string Stopped => StoppedBox.Text;

    public string Next => NextBox.Text;

    public IReadOnlyList<string> Links => Lines(LinksBox.Text);

    public IReadOnlyList<string> Thoughts => Lines(ThoughtsBox.Text);

    /// <summary>What it said once saved, to show in Ask.</summary>
    public string? Reply { get; private set; }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (new[] { Project, Stopped, Next }.All(string.IsNullOrWhiteSpace) && Links.Count == 0 && Thoughts.Count == 0)
        {
            Problem.Text = "Write where you stopped or what's next, then save.";
            Problem.Visibility = Visibility.Visible;
            LiveRegion.Announce(Problem);
            return;
        }

        Reply = _save(this).Text;
        DialogResult = true;
    }

    private static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
}
