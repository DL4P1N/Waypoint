using System.Windows;

namespace Waypoint.App;

/// <summary>
/// Asks before written notes replace text the user has in a note. Keeping their version is the default,
/// so a stray Enter never replaces anything. <see cref="Ask"/> and <see cref="Tell"/> use it for the app's other
/// questions too, such as restoring a backup, so they look like the rest of Waypoint instead of a system box.
/// </summary>
public partial class NoteConfirmWindow : Window
{
    public NoteConfirmWindow(Window? owner, string question, string detail, string? go, string stay = "Keep my version", string? title = null)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        if (owner is { IsVisible: true })
        {
            Owner = owner;
        }
        else
        {
            // Asked from the tray with the window closed, so it shows on its own, in front.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;
        }

        if (title is not null)
        {
            Title = title;
        }

        Question.Text = question;
        Detail.Text = detail;
        Detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = stay;
        GoButton.Content = go;
        GoButton.Visibility = go is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>A yes or no question. Staying is the default, so Enter or Esc never goes ahead.</summary>
    public static bool Ask(Window? owner, string title, string question, string detail, string go, string stay) =>
        new NoteConfirmWindow(owner, question, detail, go, stay, title).ShowDialog() == true;

    /// <summary>Something to read, with only OK.</summary>
    public static void Tell(Window? owner, string title, string text, string detail = "") =>
        new NoteConfirmWindow(owner, text, detail, null, "OK", title).ShowDialog();

    private void OnGoClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
