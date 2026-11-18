using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Files;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>Held notices, which are kept until they've gone out to Discord.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>Held notices going out now, so a slow send isn't started again on the next round.</summary>
    private readonly HashSet<long> _heldSending = [];

    /// <summary>
    /// The held notices not already going out. They're read inside the guard, so one that has just gone out and
    /// been let go is never read before its send is over and started again.
    /// </summary>
    private List<HeldNotice> TakeHeldToSend()
    {
        lock (_heldSending)
        {
            var held = _database.GetHeldNotices();

            // A note is let go in the same write as its notice, so one left without its notice is stale, and a later
            // notice given the same id would otherwise take its message and never go out.
            var keys = held.Select(notice => HeldSendKey(notice.Id)).ToHashSet(StringComparer.Ordinal);
            foreach (var send in _database.GetDiscordSends("held:"))
            {
                if (!keys.Contains(send.Key))
                {
                    _database.FinishDiscordSend(send.Key);
                }
            }

            return held.Where(notice => _heldSending.Add(notice.Id)).ToList();
        }
    }

    /// <summary>
    /// A held notice is kept until it's gone out, and noted before it's sent, so a crash partway sends it once:
    /// never lost, and never twice. It's let go in the same write that notes it's done.
    /// </summary>
    private static string HeldSendKey(long id) => $"held:{id}";

    private async Task SendHeldNoticeAsync(HeldNotice notice)
    {
        var key = HeldSendKey(notice.Id);
        try
        {
            await SendNoticeAsync(notice.Title, notice.Text, notice.Tone, notice.ToBudget, sendKey: key);
            _database.InOneWrite(() =>
            {
                _database.DropHeldNotice(notice.Id);
                _database.FinishDiscordSend(key);
                return 0;
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logError(exception);
        }
        finally
        {
            lock (_heldSending)
            {
                _heldSending.Remove(notice.Id);
            }
        }
    }

}
