using System.Windows;
using System.Windows.Automation;

namespace Waypoint.App;

/// <summary>Asks for a name. The same box names something new when given a different button, such as Add.</summary>
public partial class RenameWindow : Window
{
    /// <param name="heading">Said at the top and used as the window's name, the way the other forms have one.</param>
    public RenameWindow(Window owner, string name, string button = "Rename", string? hint = null, string heading = "Rename")
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        Title = heading;
        Heading.Text = heading;
        NameBox.Text = name;
        NameBox.Tag = hint;
        AutomationProperties.SetHelpText(NameBox, hint ?? "");
        DoneButton.Content = button;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>
    /// For "Pair with server..." in the tray menu. Its window, box, and button each say what they're for, so a screen
    /// reader or UI Automation finds it as the pairing form rather than another Rename.
    /// </summary>
    public static RenameWindow ForPairing(Window owner)
    {
        var form = new RenameWindow(owner, "", "Pair", "The token the server showed when it first started", "Pair with server");
        AutomationProperties.SetName(form.NameBox, "Server token");
        AutomationProperties.SetAutomationId(form.NameBox, "PairToken");
        AutomationProperties.SetAutomationId(form, "PairWithServer");
        form.EmptyProblem = "Paste the server's token first.";
        return form;
    }

    public string NewName => NameBox.Text.Trim();

    /// <summary>
    /// Said under the box when the button is pressed with it empty, so pressing it is never met with silence. Null
    /// when an empty box means something to whoever opened the form, such as removing a link.
    /// </summary>
    public string? EmptyProblem { get; set; } = "Type a name first.";

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (NewName.Length > 0)
        {
            DialogResult = true;
            return;
        }

        if (EmptyProblem is { } problem)
        {
            Problem.Text = problem;
            Problem.Visibility = Visibility.Visible;
            LiveRegion.Announce(Problem);
        }

        NameBox.Focus();
    }

    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (Problem is not null && NameBox.Text.Trim().Length > 0)
        {
            Problem.Visibility = Visibility.Collapsed;
        }
    }
}
