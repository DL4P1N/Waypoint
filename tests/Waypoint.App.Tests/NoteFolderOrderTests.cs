using System.Windows;
using System.Windows.Controls;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>Dragging note folders into a new order on the Notes page.</summary>
public sealed class NoteFolderOrderTests
{
    private static (NotesView View, NestedColumn Column) Make(UiTest ui)
    {
        var view = new NotesView();
        view.Connect(ui.Database);
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();
        return (view, UiTest.Named<NestedColumn>(view, "Column"));
    }

    private static IReadOnlyList<string> Names(UiTest ui) => ui.Database.GetNoteFolders().Select(folder => folder.Name).ToList();

    [Fact]
    public void A_dragged_order_is_saved_whole() => Ui.Run(ui =>
    {
        var garden = ui.Database.CreateNoteFolder("Garden", null, DateTime.Now).Id;
        var recipes = ui.Database.CreateNoteFolder("Recipes", null, DateTime.Now).Id;
        var trips = ui.Database.CreateNoteFolder("Trips", null, DateTime.Now).Id;
        var (view, column) = Make(ui);

        UiTest.Call(view, "OnFoldersReordered", "top", new List<string> { $"f{trips}", $"f{garden}", $"f{recipes}" });
        ui.Pump();

        Assert.Equal(["Trips", "Garden", "Recipes"], Names(ui));
        Assert.Equal(Visibility.Collapsed, UiTest.Named<TextBlock>(column, "Problem").Visibility);
    });

    [Fact]
    public void A_dragged_order_that_cant_all_be_saved_keeps_the_order_there_was_and_says_so() => Ui.Run(ui =>
    {
        var garden = ui.Database.CreateNoteFolder("Garden", null, DateTime.Now).Id;
        var recipes = ui.Database.CreateNoteFolder("Recipes", null, DateTime.Now).Id;
        var trips = ui.Database.CreateNoteFolder("Trips", null, DateTime.Now).Id;
        var (view, column) = Make(ui);

        // Recipes was deleted elsewhere just before the drag landed. Garden isn't moved to the end without it.
        ui.Database.DeleteNoteFolder(recipes, Waypoint.Core.Data.NotesInFolder.Refuse);
        UiTest.Call(view, "OnFoldersReordered", "top", new List<string> { $"f{trips}", $"f{recipes}", $"f{garden}" });
        ui.Pump();

        Assert.Equal(["Garden", "Trips"], Names(ui));
        var problem = UiTest.Named<TextBlock>(column, "Problem");
        Assert.Equal(Visibility.Visible, problem.Visibility);
        Assert.Equal("Those folders couldn't be put in that order.", problem.Text);
    });
}
