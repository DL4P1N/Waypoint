using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>A month's purchases on the phone, a page at a time, from /purchases and the budget card.</summary>
public sealed partial class WaypointWorker
{
    private const string PurchasesTarget = "purchases:";
    private const int PurchasesPerPage = 20;

    /// <summary>Purchases on the budget view and /budget, which opens this month's purchases.</summary>
    public const string PurchasesOpen = "purchases";

    /// <summary>A month's purchases, newest first, a page at a time, with the month before a press away.</summary>
    private DiscordCard PurchasesCard(DateOnly month, int page)
    {
        var today = DateOnly.FromDateTime(_clock());
        var start = new DateOnly(month.Year, month.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var rows = _database.GetSpending(start, end < today ? end : today)
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.At ?? TimeOnly.MinValue)
            .Select(row => Shorten($"• {Day(row.Date)} · {DiscordText.Escape(MerchantName.Clean(row.Title))} · {Briefing.Money(row.AmountCents)} · {DiscordText.Escape(row.Category)}", 300))
            .ToList();
        var pages = DiscordPages.Split(rows, row => row.Length + 1, NoteText, PurchasesPerPage);
        page = DiscordPages.Clamp(page, pages.Count);
        var key = start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var months = new List<CardAction> { new("Month before", DiscordPages.Id(PurchasesTarget + start.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture), 0), ButtonRole.Other) };
        if (start.AddMonths(1) <= today)
        {
            months.Add(new CardAction("Month after", DiscordPages.Id(PurchasesTarget + start.AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture), 0), ButtonRole.Other));
        }

        var title = DiscordPages.Titled($"Purchases in {start.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}", page, pages.Count);
        return new DiscordCard(title, rows.Count == 0 ? "No purchases this month." : string.Join("\n", pages[page]), [], CardTone.Money)
        {
            MoreRows = DiscordViews.Rows([.. DiscordPages.Pager(PurchasesTarget + key, page, pages.Count), .. months]),
        };
    }

    /// <summary>The budget card with a button for this month's purchases.</summary>
    private DiscordCard BudgetWithPurchases(DateOnly today) =>
        DiscordViews.BudgetCard(BudgetLines(today)) with { Actions = [new CardAction("Purchases", PurchasesOpen, ButtonRole.Other)] };

    private DiscordCard? PurchasesPage(string month, int page) =>
        DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ? PurchasesCard(start, page) : null;
}
