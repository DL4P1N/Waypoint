using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Email;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string RepaymentPending = "pending";
    private const string RepaymentMatched = "matched";
    private const string RepaymentCleared = "cleared";
    private const int RepaymentLookBackDays = 21;
    private const int TypedMatchLookBackDays = 60;

    /// <summary>A card has room for four purchases beside Not a repayment.</summary>
    public const int MaxCardCandidates = 4;

    /// <summary>A whole merchant name found inside what was typed has to be this long, so "Deli" counts but "Ax" doesn't.</summary>
    private const int MinWholeName = 4;

    /// <summary>A repayment under a tenth of a charge is too small to pin on it without a note saying so.</summary>
    private const int SmallestShareOfCharge = 10;

    private static readonly string[] FoodWords = ["food", "dining", "restaurant", "takeout", "meal", "grocer", "eat"];

    /// <summary>
    /// Stores a new payment and settles it if there's one clear purchase. Returns null for an email
    /// that's been read before, so nothing is counted or asked twice.
    /// </summary>
    public PaymentAppOutcome? AddRepayment(string messageId, PaymentAppPayment payment, DateOnly received)
    {
        var food = FoodCategories();

        // Kept and settled in one write, so a failure between can't leave it counted on the purchase but still waiting.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO repayments (message_id, payer, cents, note, received_on, status)
                VALUES ($id, $payer, $cents, $note, $received, $status)
                """;
            insert.Parameters.AddWithValue("$id", messageId);
            insert.Parameters.AddWithValue("$payer", payment.Payer);
            insert.Parameters.AddWithValue("$cents", payment.Cents);
            insert.Parameters.AddWithValue("$note", (object?)payment.Note ?? DBNull.Value);
            insert.Parameters.AddWithValue("$received", received.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$status", RepaymentPending);
            if (insert.ExecuteNonQuery() == 0)
            {
                return null;
            }
        }

        var money = Briefing.Money(payment.Cents);
        if (ClearMatch(connection, food, payment, received) is not { } match)
        {
            transaction.Commit();
            var reason = payment.Note is { } note ? $" for {note}" : "";
            return new PaymentAppOutcome($"{payment.Payer} paid you {money}{reason}. Which purchase was it for?", false, messageId);
        }

        SettleRepayment(connection, messageId, match.Id, payment.Cents);
        transaction.Commit();
        return new PaymentAppOutcome($"{payment.Payer} paid you {money}, so it's marked repaid on {match.Title}", true, messageId);
    }

    /// <summary>Oldest first, so they're answered in the order they came.</summary>
    public IReadOnlyList<PaymentAppRepayment> GetWaitingRepayments()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id, payer, cents, note, received_on FROM repayments WHERE status = $status ORDER BY received_on, rowid";
        command.Parameters.AddWithValue("$status", RepaymentPending);

        var waiting = new List<PaymentAppRepayment>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            waiting.Add(ReadRepayment(reader));
        }

        return waiting;
    }

    /// <summary>Keeps which Discord card asks about a payment, so a reply to it or a button on it is about that payment, even after a restart.</summary>
    public void SetRepaymentCard(string messageId, string cardMessageId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE repayments SET card_message_id = $card WHERE message_id = $id";
        command.Parameters.AddWithValue("$card", cardMessageId);
        command.Parameters.AddWithValue("$id", messageId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The payment a Discord card asks about, by the card's ID. A card sent before IDs were kept is known by what it
    /// says, "{payer} paid you {money}", for a payment that's still waiting, and its ID is kept from then on.
    /// Null when the card isn't about a payment.
    /// </summary>
    public RepaymentCard? FindRepaymentForCard(string cardMessageId, string? cardText = null)
    {
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT message_id, payer, cents, note, received_on, status FROM repayments WHERE card_message_id = $card";
            command.Parameters.AddWithValue("$card", cardMessageId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new RepaymentCard(ReadRepayment(reader), reader.GetString(5) == RepaymentPending);
            }
        }

        // Discord shows names with formatting marks escaped, so they're taken out before comparing.
        if (cardText?.Replace("\\", "", StringComparison.Ordinal) is not { Length: > 0 } said)
        {
            return null;
        }

        var waiting = GetWaitingRepayments()
            .FirstOrDefault(repayment => said.Contains($"{repayment.Payer} paid you {Briefing.Money(repayment.Cents)}", StringComparison.OrdinalIgnoreCase)
                && CardOf(connection, repayment.MessageId) is null);
        if (waiting is null)
        {
            return null;
        }

        SetRepaymentCard(waiting.MessageId, cardMessageId);
        return new RepaymentCard(waiting, true);
    }

    private static string? CardOf(SqliteConnection connection, string messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT card_message_id FROM repayments WHERE message_id = $id";
        command.Parameters.AddWithValue("$id", messageId);
        return command.ExecuteScalar() as string;
    }

    private static PaymentAppRepayment ReadRepayment(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture));

    /// <summary>Ties a waiting repayment to the purchase the user named. The amount can't go past the charge.</summary>
    public OpReply MatchRepayment(string? payer, string? purchase, DateOnly today)
    {
        if (FindWaiting(payer) is not { } repayment)
        {
            return new OpReply("No repayment is waiting");
        }

        return MatchNamed(repayment, purchase, today);
    }

    /// <summary>The same typed match, for one exact payment, such as a reply to its card.</summary>
    public OpReply MatchRepaymentNamed(string messageId, string? purchase, DateOnly today) =>
        GetWaitingRepayments().FirstOrDefault(waiting => waiting.MessageId == messageId) is { } repayment
            ? MatchNamed(repayment, purchase, today)
            : new OpReply("That repayment is already settled");

    private OpReply MatchNamed(PaymentAppRepayment repayment, string? purchase, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(purchase))
        {
            return new OpReply($"Which purchase was {repayment.Payer}'s {Briefing.Money(repayment.Cents)} for?", true);
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var from = today.AddDays(-TypedMatchLookBackDays);
        var named = Squash(purchase);

        // A purchase whose name holds what was typed beats one whose name is only inside it. Then one the money
        // fits inside comes first, then the newest, which is the order they're read in.
        var matches = Purchases(connection, from, today)
            .Select(row => (Row: row, Fit: NameFit(row.Title, named)))
            .Where(match => match.Fit > 0)
            .OrderByDescending(match => match.Fit)
            .ThenByDescending(match => match.Row.LeftCents >= repayment.Cents)
            .Select(match => match.Row)
            .ToList();
        if (matches.Count == 0)
        {
            return new OpReply($"Couldn't find a recent {purchase.Trim()} purchase. {DescribeCandidates(repayment)}");
        }

        var reply = Settle(connection, repayment, matches[0].Id, matches[0].Title, matches[0].LeftCents);
        transaction.Commit();
        return reply;
    }

    /// <summary>The purchases it could be for, so a name that didn't match can be typed again or picked.</summary>
    private string DescribeCandidates(PaymentAppRepayment repayment)
    {
        var candidates = GetRepaymentCandidates(repayment, MaxCardCandidates);
        return candidates.Count == 0
            ? "There are no unrepaid purchases from the few weeks before it."
            : $"Recent ones: {string.Join("; ", candidates.Select(Describe))}.";
    }

    /// <summary>A candidate in a few words: the merchant, the day, and what's still unrepaid on it.</summary>
    public static string Describe(RepaymentCandidate candidate) =>
        $"{candidate.Title}, {candidate.Date.ToString("MMM d", CultureInfo.InvariantCulture)}, {Briefing.Money(candidate.LeftCents)} left";

    /// <summary>
    /// Whether a typed name is this purchase. Case, apostrophes, spaces, punctuation and store numbers don't count,
    /// so "Trader Joes" is "Trader Joe's" and "TRADER JOES #123". The merchant's clean name counts too, and a longer typed
    /// name such as "trader joe's groceries" still finds "Trader Joe's".
    /// </summary>
    public static bool Names(string title, string typed) => NameFit(title, Squash(typed)) > 0;

    /// <summary>2 when the purchase's name holds what was typed, 1 when the typed words hold its whole name, 0 when neither.</summary>
    private static int NameFit(string title, string named)
    {
        if (named.Length == 0)
        {
            return 0;
        }

        var shown = new[] { Squash(title), Squash(MerchantName.Clean(title)) }.Where(name => name.Length > 0).ToList();
        if (shown.Any(name => name.Contains(named, StringComparison.Ordinal)))
        {
            return 2;
        }

        return shown.Any(name => name.Length >= MinWholeName && named.Contains(name, StringComparison.Ordinal)) ? 1 : 0;
    }

    /// <summary>Letters only, in lower case.</summary>
    private static string Squash(string text) =>
        string.Concat(text.Where(char.IsLetter).Select(char.ToLowerInvariant));

    /// <summary>
    /// Purchases a waiting repayment could be for, for the Budget page to offer: unrepaid charges from the
    /// few weeks before it came, ones it fits inside first, newest first within that.
    /// </summary>
    public IReadOnlyList<RepaymentCandidate> GetRepaymentCandidates(PaymentAppRepayment repayment, int limit = 5)
    {
        using var connection = Open();
        return Purchases(connection, repayment.ReceivedOn.AddDays(-RepaymentLookBackDays), repayment.ReceivedOn)
            .OrderByDescending(row => row.LeftCents >= repayment.Cents)
            .Take(limit)
            .Select(row => new RepaymentCandidate(row.Id, MerchantName.Clean(row.Title), row.Date, row.LeftCents))
            .ToList();
    }

    /// <summary>Ties a waiting repayment to the purchase picked on the Budget page, the same way a typed match does.</summary>
    public OpReply MatchRepaymentTo(string messageId, long itemId)
    {
        if (GetWaitingRepayments().FirstOrDefault(waiting => waiting.MessageId == messageId) is not { } repayment)
        {
            return new OpReply("That repayment is already settled");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var match = Purchases(connection, DateOnly.MinValue, DateOnly.MaxValue).FirstOrDefault(row => row.Id == itemId);
        if (match.Title is null)
        {
            return new OpReply("Couldn't find that purchase");
        }

        var reply = Settle(connection, repayment, match.Id, match.Title, match.LeftCents);
        transaction.Commit();
        return reply;
    }

    /// <summary>Run inside the caller's write. One settled meanwhile, from another card or the Budget page, adds nothing.</summary>
    private static OpReply Settle(SqliteConnection connection, PaymentAppRepayment repayment, long itemId, string title, long leftCents)
    {
        var applied = Math.Min(repayment.Cents, leftCents);
        return SettleRepayment(connection, repayment.MessageId, itemId, applied)
            ? new OpReply($"Marked {Briefing.Money(applied)} from {repayment.Payer} repaid on {title}")
            : new OpReply("That repayment is already settled");
    }

    public OpReply ClearRepayment(string? payer) =>
        FindWaiting(payer) is { } repayment ? Clear(repayment) : new OpReply("No repayment is waiting");

    /// <summary>The Budget page's "Not a repayment", for one exact payment.</summary>
    public OpReply ClearRepaymentById(string messageId) =>
        GetWaitingRepayments().FirstOrDefault(waiting => waiting.MessageId == messageId) is { } repayment
            ? Clear(repayment)
            : new OpReply("That repayment is already settled");

    private OpReply Clear(PaymentAppRepayment repayment)
    {
        using var connection = Open();
        SetRepaymentStatus(connection, repayment.MessageId, RepaymentCleared, null);
        return new OpReply($"{repayment.Payer}'s {Briefing.Money(repayment.Cents)} won't count as a repayment");
    }

    private PaymentAppRepayment? FindWaiting(string? payer)
    {
        var waiting = GetWaitingRepayments();
        return string.IsNullOrWhiteSpace(payer)
            ? waiting.FirstOrDefault()
            : waiting.FirstOrDefault(repayment => repayment.Payer.Contains(payer.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The one purchase this has to be for: a recent food charge it fits inside. If a note narrows several
    /// down to one, that's clear too. Anything less certain is left for the user.
    /// </summary>
    private static (long Id, string Title, long LeftCents)? ClearMatch(SqliteConnection connection, HashSet<string> categories, PaymentAppPayment payment, DateOnly received)
    {
        var fits = Purchases(connection, received.AddDays(-RepaymentLookBackDays), received)
            .Where(row => row.Category is { } category && categories.Contains(category) && row.LeftCents >= payment.Cents)
            .ToList();

        var named = payment.Note is { } note ? fits.Where(row => SearchIndex.Mentions(row.Title, note)).ToList() : [];
        if (named.Count == 1)
        {
            return (named[0].Id, named[0].Title, named[0].LeftCents);
        }

        return fits.Count == 1 && payment.Cents * SmallestShareOfCharge >= fits[0].LeftCents
            ? (fits[0].Id, fits[0].Title, fits[0].LeftCents)
            : null;
    }

    /// <summary>The categories a repayment can be matched in without asking, read before a write opens.</summary>
    private HashSet<string> FoodCategories() =>
        GetCategories()
            .Select(category => category.Name)
            .Where(name => FoodWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Charges with money still unrepaid, newest first.</summary>
    private static List<(long Id, string Title, string? Category, long LeftCents, DateOnly Date)> Purchases(SqliteConnection connection, DateOnly from, DateOnly through)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.id, i.title, i.category, i.amount_cents - i.reimbursed_cents, {SpendDay}
            FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Expense' AND i.canceled = 0 AND i.one_off = 0 AND i.amount_cents > i.reimbursed_cents
              AND {SpendDay} BETWEEN $from AND $through AND {CountedSql("i")}
            ORDER BY {SpendDay} DESC, i.id DESC
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", through.ToString("O", CultureInfo.InvariantCulture));

        var rows = new List<(long, string, string?, long, DateOnly)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3),
                DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>
    /// Marks a waiting repayment matched and counts it on the purchase, in the caller's write. False when it was
    /// no longer waiting, so the money is never counted on a purchase twice.
    /// </summary>
    private static bool SettleRepayment(SqliteConnection connection, string messageId, long itemId, long cents)
    {
        using (var claim = connection.CreateCommand())
        {
            claim.CommandText = "UPDATE repayments SET status = $status, item_id = $item WHERE message_id = $id AND status = $pending";
            claim.Parameters.AddWithValue("$status", RepaymentMatched);
            claim.Parameters.AddWithValue("$item", itemId);
            claim.Parameters.AddWithValue("$id", messageId);
            claim.Parameters.AddWithValue("$pending", RepaymentPending);
            if (claim.ExecuteNonQuery() != 1)
            {
                return false;
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET reimbursed_cents = MIN(amount_cents, reimbursed_cents + $cents) WHERE id = $id";
        command.Parameters.AddWithValue("$cents", cents);
        command.Parameters.AddWithValue("$id", itemId);
        command.ExecuteNonQuery();
        return true;
    }

    private static void SetRepaymentStatus(SqliteConnection connection, string messageId, string status, long? itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE repayments SET status = $status, item_id = $item WHERE message_id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$item", (object?)itemId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", messageId);
        command.ExecuteNonQuery();
    }
}

/// <summary>The payment a Discord card asks about, and whether it's still waiting for an answer.</summary>
public sealed record RepaymentCard(PaymentAppRepayment Repayment, bool Waiting);

/// <summary>A purchase a waiting repayment might be for. LeftCents is what's still unrepaid on it.</summary>
public sealed record RepaymentCandidate(long ItemId, string Title, DateOnly Date, long LeftCents);
