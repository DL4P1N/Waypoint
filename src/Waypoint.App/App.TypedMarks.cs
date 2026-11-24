using System;
using System.Threading.Tasks;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>Saying once in Ask what the repair of notes' typed headings, lists, and quotes changed.</summary>
public partial class App
{
    /// <summary>
    /// Says in Ask, once, what the repair changed where the notes are kept, here or on the server. Nothing is said
    /// when nothing was repaired, or while the server can't be reached.
    /// </summary>
    private void SayTypedMarksRepair(IPreferences notes) => _ = Task.Run(() =>
    {
        try
        {
            if (WaypointDatabase.TakeTypedMarksMessage(notes) is { } message)
            {
                Dispatcher.BeginInvoke(() => _itemsWindow.ShowReply(message));
            }
        }
        catch (Exception exception) when (exception is ServerUnavailableException or ServerTimedOutException)
        {
            // Said the next time Waypoint opens, once the server answers.
        }
    });
}
