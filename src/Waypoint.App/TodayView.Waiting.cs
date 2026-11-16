using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using Waypoint.Core.Capture;

namespace Waypoint.App;

/// <summary>
/// Waiting on someone, on the To do page: a fixed Waiting on entry in the column, a + form, and row menu
/// actions for following up. Waits that aren't due yet stay off the other filters unless searched for.
/// </summary>
public partial class TodayView
{
    private const string WaitingKey = "waiting";

    private IReadOnlyDictionary<long, WaitingItem> _waiting = new Dictionary<long, WaitingItem>();

    private bool ShowingWaiting => Column.SelectedKey == WaitingKey;

    private void LoadWaiting() => _waiting = _database.GetWaiting().ToDictionary(waiting => waiting.ItemId);

    private WaitingItem? WaitingFor(StoredItem item) =>
        item.Source == ItemSource.Capture ? _waiting.GetValueOrDefault(item.Id) : null;

    private ColumnEntry WaitingEntry() =>
        new(WaitingKey, _waiting.Count > 0 ? $"Waiting on  ·  {_waiting.Count}" : "Waiting on");

    /// <summary>
    /// The Waiting on entry shows only waits. Anywhere else, one that isn't due yet is left out, unless a
    /// search is typed, so it can always be found.
    /// </summary>
    private bool PassesWaiting(StoredItem item, string search, DateOnly today)
    {
        var waiting = WaitingFor(item);
        return ShowingWaiting
            ? waiting is not null
            : waiting is null || waiting.IsDue(today) || search.Trim().Length > 0;
    }

    private string WaitingSignature() =>
        string.Join(',', _waiting.Values.Select(waiting => $"{waiting.ItemId}:{waiting.FollowUpOn}:{waiting.RepliedAt}"));

    /// <summary>What a row says about its wait, such as "Waiting on Prof. Quill until Thu, Nov 12".</summary>
    private static string? WaitingDetail(WaitingItem? waiting) => waiting switch
    {
        null => null,
        { RepliedAt: not null } => $"{waiting.Who} wrote back",
        _ => $"Waiting on {waiting.Who} until {waiting.FollowUpOn.ToString("ddd, MMM d", CultureInfo.CurrentCulture)}",
    };

    /// <summary>A new wait from +. Only Who needs typing, and it starts in Waiting on so it can be seen.</summary>
    private void AddWaiting()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var form = new WaitingWindow(Window.GetWindow(this), today, null, form =>
        {
            var added = _database.AddWaitingByHand(form.Who, form.Topic, form.FollowUp, form.Email, DateTime.Now);
            if (added.Item is { } stored)
            {
                Bar.Show($"Added {stored.Title}", about: About(stored));
            }

            return added.Problem;
        });

        if (form.ShowDialog() == true)
        {
            Column.Select(WaitingKey);
            Acted();
        }
    }

    /// <summary>The row menu's waiting actions: start waiting on a task, or for a wait, follow up, wait longer, or stop.</summary>
    private IEnumerable<RowMenuEntry> WaitingMenu(StoredItem item, DateOnly today)
    {
        if (item.Source != ItemSource.Capture || item.Done || item.ParentRef is not null)
        {
            return [];
        }

        if (WaitingFor(item) is not { } waiting)
        {
            return item.Kind is ItemKind.Task or ItemKind.Deadline ? [new RowMenuEntry("Waiting on someone...", () => MarkWaiting(item, today))] : [];
        }

        return
        [
            new RowMenuEntry("Copy follow-up draft", () => DraftFollowUp(waiting)),
            new RowMenuEntry("Still waiting", Children:
            [
                new("A few more days", () => StillWaiting(waiting, null)),
                new("Next week", () => StillWaiting(waiting, today.AddDays(7))),
                new("Pick a date...", () => PickWaitingDate(waiting, today)),
            ]),
            new RowMenuEntry("Not waiting anymore", () => StopWaiting(waiting)),
        ];
    }

    private void MarkWaiting(StoredItem item, DateOnly today)
    {
        var form = new WaitingWindow(Window.GetWindow(this), today, item.Title, form =>
            _database.MarkWaitingByHand(item, form.Who, form.FollowUp, form.Email, DateTime.Now));
        if (form.ShowDialog() == true)
        {
            Bar.Show($"Waiting on {form.Who}");
            Acted();
        }
    }

    /// <summary>
    /// Copies the draft, and with an address known, opens a new email holding it in the user's mail app.
    /// Nothing is ever sent from here.
    /// </summary>
    private void DraftFollowUp(WaitingItem waiting)
    {
        var draft = WaitingOn.Draft(waiting);
        RowMenu.Copy(draft);
        if (waiting.Email is { } email)
        {
            var subject = Uri.EscapeDataString($"Following up: {waiting.Topic ?? waiting.Title}");
            try
            {
                Process.Start(new ProcessStartInfo($"mailto:{email}?subject={subject}&body={Uri.EscapeDataString(draft)}") { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                // No mail app is set up. The copy is still there to paste.
            }
        }

        Bar.Show($"Copied a follow-up for {waiting.Who}. Nothing was sent.");
    }

    private void StillWaiting(WaitingItem waiting, DateOnly? until)
    {
        _reply(_database.StillWaiting(waiting.ItemId, until, DateTime.Now).Text);
        Acted();
    }

    private void PickWaitingDate(WaitingItem waiting, DateOnly today)
    {
        var picker = new PickDateWindow(Window.GetWindow(this), waiting.Title, WaitingOn.DefaultFollowUp(today), null, asksForTime: false, action: "Wait until then");
        if (picker.ShowDialog() == true)
        {
            StillWaiting(waiting, picker.Date);
        }
    }

    private void StopWaiting(WaitingItem waiting)
    {
        if (_database.StopWaiting(waiting.ItemId))
        {
            Bar.Show($"Not waiting on {waiting.Who} anymore");
        }

        Acted();
    }
}
