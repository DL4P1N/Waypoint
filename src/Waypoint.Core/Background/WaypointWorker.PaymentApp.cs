using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>
/// Answers to a payment app card in Discord. A reply to the card names the purchase and is matched straight away,
/// without sorting, so it can never be read as a note or a task.
/// </summary>
public sealed partial class WaypointWorker
{
    /// <summary>A purchase button on a payment app card. The purchase's ID follows, and the card's own ID says which payment.</summary>
    public const string PaymentAppPick = "repay-pick:";

    /// <summary>Not a repayment on a payment app card.</summary>
    public const string PaymentAppNone = "repay-none";

    /// <summary>A button for each purchase it could be for, merchant, day and what's left, then Not a repayment last, as Skip is.</summary>
    private static List<CardAction> PaymentAppButtons(IReadOnlyList<RepaymentCandidate> candidates) =>
        candidates
            .Select(candidate => new CardAction(WaypointDatabase.Describe(candidate), PaymentAppPick + candidate.ItemId.ToString(CultureInfo.InvariantCulture)))
            .Append(new CardAction(CardLabels.NotARepayment, PaymentAppNone, ButtonRole.Skip))
            .ToList();

    /// <summary>
    /// A button on a payment app card, settled through the same matching as the Budget page. Null when the button isn't
    /// one of these. The card turns gray and says what was done.
    /// </summary>
    private ButtonResult? OnPaymentAppButton(string action, string card)
    {
        var picked = TakeId(action, PaymentAppPick);
        if (picked is null && action != PaymentAppNone)
        {
            return null;
        }

        if (_database.FindRepaymentForCard(card) is not { } asked)
        {
            return new ButtonResult(Outcome: "Couldn't tell which payment this card is about. Match it on the Budget page.");
        }

        if (!asked.Waiting)
        {
            return new ButtonResult(Outcome: "That repayment is already settled");
        }

        var payment = asked.Repayment.MessageId;
        var reply = picked is { } item ? _database.MatchRepaymentTo(payment, item) : _database.ClearRepaymentById(payment);
        if (_database.GetWaitingRepayments().FirstOrDefault(waiting => waiting.MessageId == payment) is not { } still)
        {
            return new ButtonResult(Outcome: reply.Text);
        }

        // That purchase was paid back meanwhile, so the card is drawn again with what's left to pick.
        var candidates = _database.GetRepaymentCandidates(still, WaypointDatabase.MaxCardCandidates);
        return new ButtonResult(DiscordText.Notice(BankProfiles.Current.PaymentApp.Name, $"{reply.Text}\nPick another, or reply with its name.", CardTone.Attention) with { Actions = PaymentAppButtons(candidates) });
    }

    /// <summary>Typed replies that mean the payment wasn't paying anything back. Apostrophes and end marks are taken off first.</summary>
    private static readonly HashSet<string> NotARepaymentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not a repayment", "not repayment", "no repayment", "wasnt a repayment", "it wasnt a repayment", "isnt a repayment",
        "skip", "skip it", "none", "none of them", "none of these", "neither", "no", "nope", "ignore", "ignore it", "not one",
    };

    /// <summary>Whether a typed reply to a payment app card says it wasn't a repayment.</summary>
    public static bool SaysNotARepayment(string text)
    {
        var plain = string.Join(' ', text.Replace("'", "", StringComparison.Ordinal).Replace("’", "", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', '!');
        return NotARepaymentWords.Contains(plain);
    }

    /// <summary>
    /// Settles the card's payment with what was typed, or clears it, and says so under the reply. Once it's settled,
    /// the card turns gray like any handled card. A name that isn't found leaves it waiting and lists what it could be.
    /// </summary>
    private void AnswerPaymentAppReply(DiscordMessage message, string card, RepaymentCard asked)
    {
        var payment = asked.Repayment.MessageId;
        OpReply reply;
        if (!asked.Waiting)
        {
            reply = new OpReply("That repayment is already settled");
        }
        else if (SaysNotARepayment(message.Text))
        {
            reply = _database.ClearRepaymentById(payment);
        }
        else
        {
            reply = _database.MatchRepaymentNamed(payment, message.Text.Trim(), DateOnly.FromDateTime(_clock()));
        }

        var settled = asked.Waiting && !_database.GetWaitingRepayments().Any(waiting => waiting.MessageId == payment);
        _viewsDirty = true;
        _host.Changed();
        ViewsChanged();
        if (Discord is not { } discord)
        {
            return;
        }

        _ = discord.ReplyAsync(message.Id, DiscordText.Reply(reply.Text, settled ? CardTone.Finished : CardTone.Attention));
        if (settled)
        {
            _ = discord.MarkCaptureCardHandledAsync(card, reply.Text);
        }
    }
}
