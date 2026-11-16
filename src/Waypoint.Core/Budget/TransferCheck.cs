using System.Globalization;
using System.Text.RegularExpressions;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Budget;

public enum TransferState
{
    Confirmed,
    NotSeenYet,
    Missed,
}

/// <summary>Money seen moving: a checking export row going out, or a brokerage deposit email coming in. Cents is positive.</summary>
public sealed record TransferSighting(DateOnly Date, long Cents, string Description, bool FromEmail);

/// <summary>One past scheduled transfer and whether it was seen happening.</summary>
public sealed record TransferStatus(string Label, DateOnly Due, long Cents, TransferState State, string Note, bool Investing);

/// <summary>
/// Checks past scheduled savings transfers and investing purchases against what Waypoint has actually seen: checking
/// exports and brokerage deposit emails. Only a checking export that covers the days after one can show it was
/// missed, since an email that never came proves nothing. Without either source it says so instead of guessing.
/// </summary>
public static partial class TransferCheck
{
    /// <summary>Days a transfer gets to show up before it counts as missed, since bank transfers take a few business days.</summary>
    public const int GraceDays = 4;

    public const int LookBackDays = 35;

    /// <summary>A transfer can post a little before its date, such as one sent early ahead of a weekend.</summary>
    private const int EarlyDays = 3;

    private const int LateDays = 7;
    private const long CloseCents = 100;

    /// <summary>
    /// Each scheduled transfer that fell in the last few weeks, oldest first. AddedOn keeps one added today from
    /// reading as missed for dates before it existed. CheckingThrough is the last day a checking export covers.
    /// </summary>
    public static IReadOnlyList<TransferStatus> Check(
        DateOnly today,
        IReadOnlyList<ScheduledItem> schedule,
        IReadOnlyDictionary<long, DateOnly> addedOn,
        IReadOnlyList<TransferSighting> sightings,
        DateOnly? checkingThrough)
    {
        var due = new List<(ScheduledItem Item, DateOnly Day)>();
        foreach (var item in schedule.Where(item => item.IsBill && item.Account is null && SavingsPlan.IsTransfer(item.Label)))
        {
            var from = today.AddDays(-LookBackDays);
            if (addedOn.TryGetValue(item.Id, out var added) && added > from)
            {
                from = added;
            }

            for (var day = from; day < today; day = day.AddDays(1))
            {
                if (BudgetMath.Falls(item, day))
                {
                    due.Add((item, day));
                }
            }
        }

        var unused = sightings.ToList();
        var statuses = new List<TransferStatus>();
        foreach (var (item, day) in due.OrderBy(entry => entry.Day))
        {
            var cents = -item.AmountOn(day);
            var investing = SavingsPlan.IsInvesting(item.Label);
            var seen = unused
                .Where(sighting => Fits(sighting, item.Label, day, cents))
                .OrderBy(sighting => Math.Abs(sighting.Date.DayNumber - day.DayNumber))
                .FirstOrDefault();
            if (seen is not null)
            {
                unused.Remove(seen);
                var where = seen.FromEmail ? $"a {BankProfiles.Current.Brokerage.Name} deposit email" : "the checking export";
                statuses.Add(new TransferStatus(item.Label, day, cents, TransferState.Confirmed, $"Seen in {where} {Short(seen.Date)}", investing));
                continue;
            }

            var deadline = day.AddDays(GraceDays);
            if (today <= deadline)
            {
                statuses.Add(new TransferStatus(item.Label, day, cents, TransferState.NotSeenYet, "Transfers can take a few business days", investing));
            }
            else if (checkingThrough is { } through && through >= deadline)
            {
                statuses.Add(new TransferStatus(item.Label, day, cents, TransferState.Missed, $"Not in the checking export through {Short(through)}", investing));
            }
            else
            {
                var note = checkingThrough is { } last
                    ? $"Checking exports only go through {Short(last)}, so I can't tell yet"
                    : $"No checking export or {BankProfiles.Current.Brokerage.Name} deposit email shows it, so I can't tell";
                statuses.Add(new TransferStatus(item.Label, day, cents, TransferState.NotSeenYet, note, investing));
            }
        }

        return statuses;
    }

    /// <summary>The statuses for the Until payday page, with where the checking comes from.</summary>
    public static ReportSection Section(IReadOnlyList<TransferStatus> statuses, DateOnly? checkingThrough, bool hasDepositEmails)
    {
        if (statuses.Count == 0)
        {
            return new ReportSection("TRANSFERS", []);
        }

        var rows = statuses
            .OrderByDescending(status => status.Due)
            .Select(status => new ReportRow($"{status.Label}, {Short(status.Due)}: {StateText(status.State)}. {status.Note}.", Briefing.Money(status.Cents)))
            .ToList();

        var sources = new List<string>();
        if (checkingThrough is { } through)
        {
            sources.Add($"checking exports through {Short(through)}");
        }

        if (hasDepositEmails)
        {
            sources.Add($"{BankProfiles.Current.Brokerage.Name} deposit emails");
        }

        rows.Add(new ReportRow(sources.Count > 0
            ? $"Checked against {string.Join(" and ", sources)}."
            : $"Nothing to check these against yet. Import a checking CSV, or connect email for {BankProfiles.Current.Brokerage.Name} deposit alerts.", ""));
        if (statuses.Any(status => status.Investing))
        {
            rows.Add(new ReportRow($"For investing, this shows the money moving. Purchases inside {BankProfiles.Current.Brokerage.Name} aren't read.", ""));
        }

        return new ReportSection("TRANSFERS", rows);
    }

    public static string StateText(TransferState state) => state switch
    {
        TransferState.Confirmed => "Confirmed",
        TransferState.Missed => "Not seen yet",
        _ => "Not seen yet",
    };

    /// <summary>
    /// Close to the amount, near the date, and plainly a transfer: a checking row needs transfer wording or a word from
    /// the transfer's name, so rent of the same amount can't count. A brokerage deposit email is a transfer by its nature.
    /// </summary>
    private static bool Fits(TransferSighting sighting, string label, DateOnly day, long cents)
    {
        if (Math.Abs(sighting.Cents - cents) > Math.Max(CloseCents, cents / 100)
            || sighting.Date < day.AddDays(-EarlyDays)
            || sighting.Date > day.AddDays(LateDays))
        {
            return false;
        }

        return sighting.FromEmail
            || BankProfiles.Current.TransferWording().IsMatch(sighting.Description)
            || SearchIndex.Words(label).Any(word => word.Length >= 4 && sighting.Description.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static string Short(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);
}
