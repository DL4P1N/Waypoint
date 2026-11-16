using System.Windows;
using Waypoint.Core.Data;

namespace Waypoint.App;

/// <summary>
/// Asks where a folder's notes go before the folder is deleted. Cancel leaves everything as it was,
/// so notes are never deleted or moved without an answer.
/// </summary>
public partial class DeleteFolderWindow : Window
{
    public DeleteFolderWindow(Window owner, string folder, int notes, string? parent)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        Question.Text = notes == 1
            ? $"Delete {folder}? It has 1 note. Where should it go?"
            : $"Delete {folder}? It has {notes} notes. Where should they go?";

        ToUnfiledButton.Content = notes == 1 ? "Move it to Unfiled" : "Move them to Unfiled";
        if (parent is not null)
        {
            ToParentButton.Content = notes == 1 ? $"Move it to {parent}" : $"Move them to {parent}";
            ToParentButton.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => ToUnfiledButton.Focus();
    }

    public NotesInFolder Choice { get; private set; } = NotesInFolder.Refuse;

    private void OnToUnfiledClicked(object sender, RoutedEventArgs e)
    {
        Choice = NotesInFolder.MoveToUnfiled;
        DialogResult = true;
    }

    private void OnToParentClicked(object sender, RoutedEventArgs e)
    {
        Choice = NotesInFolder.MoveToParent;
        DialogResult = true;
    }
}
