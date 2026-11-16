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
        return form;
    }

    public string NewName => NameBox.Text.Trim();

    private void OnRenameClicked(object sender, RoutedEventArgs e) => DialogResult = NewName.Length > 0 ? true : null;
}
