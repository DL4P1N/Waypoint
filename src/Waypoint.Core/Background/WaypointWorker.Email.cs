using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Background;

/// <summary>Alert email, purchases and payment-app notices, and unread-alert logging.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>Does nothing until alert email is set up, so the timer can run either way.</summary>
    public Task PollEmailAsync() => Tracked(PollEmailOnceAsync);

    private async Task PollEmailOnceAsync()
    {
        if (Outlook is not { } outlook)
        {
            return;
        }

        await outlook.PollAsync(OnPurchase, OnUnreadAlert, cents => _ = NotifyAsync($"{Briefing.Money(cents)} deposit", "Added to your cushion", CardTone.Money, canWait: true), OnPaymentApp);
        _host.Changed();
    }

    /// <summary>The same read as the timer's, saying what came in, for when it's asked for in Ask.</summary>
    public Task<string> CheckEmailNowAsync() => Tracked(CheckEmailOnceAsync, "Waypoint is stopping, so email wasn't checked this time.");

    private async Task<string> CheckEmailOnceAsync()
    {
        if (Outlook is not { } outlook)
        {
            return "Alert email isn't set up yet. Add its app ID on the Settings page.";
        }

        var purchases = 0;
        var deposits = 0;
        await outlook.PollAsync(
            purchase =>
            {
                purchases++;
                OnPurchase(purchase);
            },
            OnUnreadAlert,
            cents =>
            {
                deposits++;
                _ = NotifyAsync($"{Briefing.Money(cents)} deposit", "Added to your cushion", CardTone.Money, canWait: true);
            },
            OnPaymentApp);
        _host.Changed();
        if (outlook.LastError is { } error)
        {
            return $"Couldn't read email: {error}";
        }

        var found = new[] { Count(purchases, "new purchase"), Count(deposits, "deposit") }.Where(part => part.Length > 0).ToList();
        var skipped = outlook.SkippedSender is { } note ? $" {note}" : "";
        return (found.Count == 0 ? "Checked email. Nothing new came in." : $"Checked email: {string.Join(" and ", found)}.") + skipped;

        static string Count(int count, string what) => count switch
        {
            0 => "",
            1 => $"1 {what}",
            _ => $"{count} {what}s",
        };
    }

    private void OnPurchase(EmailPurchase purchase)
    {
        var left = _database.GetSafeToSpend(DateOnly.FromDateTime(_clock())) is { } cents ? $"\nSafe to spend today: {Briefing.Money(cents)}" : "";
        var headline = $"{Briefing.Money(purchase.Transaction.AmountCents)} at {MerchantName.Clean(purchase.Transaction.Description)}";
        _ = NotifyAsync(headline, $"Card: {purchase.Account}{left}", CardTone.Money, toBudgetChannel: true, canWait: true);
    }

    private void OnPaymentApp(PaymentAppOutcome outcome) => _ = Tracked(() => PostPaymentAppAsync(outcome));

    /// <summary>
    /// Says a payment was matched, or asks which purchase it was for where typing is read. The card's ID is kept
    /// with the payment, so a reply to it settles that payment without going through sorting.
    /// </summary>
    public async Task PostPaymentAppAsync(PaymentAppOutcome outcome)
    {
        if (outcome.Matched)
        {
            await NotifyAsync("Repaid", outcome.Text, CardTone.Money, toBudgetChannel: true, canWait: true);
            return;
        }

        var waiting = _database.GetWaitingRepayments().FirstOrDefault(repayment => repayment.MessageId == outcome.MessageId);
        IReadOnlyList<RepaymentCandidate> candidates = waiting is null ? [] : _database.GetRepaymentCandidates(waiting, WaypointDatabase.MaxCardCandidates);
        var ask = candidates.Count > 0 ? "Pick the purchase below, or reply with its name." : "Reply with the purchase's name.";
        var card = await SendNoticeAsync(BankProfiles.Current.PaymentApp.Name, $"{outcome.Text}\n{ask}", CardTone.Attention, toCaptureChannel: true, actions: PaymentAppButtons(candidates, outcome.MessageId));
        if (card is not null && outcome.MessageId is { } payment)
        {
            _database.SetRepaymentCard(payment, card);
        }
    }

    /// <summary>Saved beside the database, so the parser can learn their format. Only the newest are kept.</summary>
    private void OnUnreadAlert(UnreadAlert alert) =>
        UnreadAlertLog.Append(Path.Combine(_dataDirectory, UnreadAlertsFile), alert);

}
