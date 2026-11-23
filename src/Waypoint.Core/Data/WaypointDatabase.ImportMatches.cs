using System.Globalization;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

/// <summary>
/// One real purchase can arrive from an alert email, a card or checking CSV, and typing. Every imported row is kept,
/// and one that turned out to be a purchase already counted gets a match, which keeps it out of the budget. Nothing is
/// deleted, so a match can be let go: when the typed purchase it matched is deleted, the imported row counts again.
/// </summary>
public sealed partial class WaypointDatabase
{
    private const string MatchUpgradeKey = "import_matches_backfilled";

    /// <summary>
    /// Typed is set when the match is a typed purchase, so the imported row still moves its card's balance, since the
    /// typed one can't say which card it was on. Backfilled marks matches found in data from before this, so they can
    /// be taken back.
    /// </summary>
    private void CreateImportMatchTables() => Execute("""
        CREATE TABLE IF NOT EXISTS import_matches (
            import_key TEXT PRIMARY KEY,
            item_id INTEGER NOT NULL,
            typed INTEGER NOT NULL,
            backfilled INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS import_matches_item ON import_matches (item_id);
        """);

    /// <summary>
    /// SQL that's true when the item under <paramref name="alias"/> counts in the budget: it isn't an imported row
    /// matched to a purchase still counted. A typed match that was deleted lets the row count again.
    /// </summary>
    internal static string CountedSql(string alias) =>
        $"NOT EXISTS (SELECT 1 FROM import_matches m JOIN items k ON k.id = m.item_id WHERE m.import_key = {alias}.import_key AND (m.typed = 0 OR k.canceled = 0))";

    /// <summary>
    /// SQL that's true when the item under <paramref name="alias"/> counts on its card. A row matched to a typed
    /// purchase still does, since the card was charged. A second copy of an alert email or CSV row doesn't.
    /// </summary>
    internal static string OnCardSql(string alias) =>
        $"NOT EXISTS (SELECT 1 FROM import_matches m WHERE m.import_key = {alias}.import_key AND m.typed = 0)";

    private static bool ImportExists(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE import_key = $key";
        command.Parameters.AddWithValue("$key", key);
        return (long)command.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// A row from a file has a key made from its account, or from checking, and its fields. Any other key is an
    /// alert email's message ID.
    /// </summary>
    private static bool IsFileKey(string account, string key) =>
        key.StartsWith($"{account}|", StringComparison.Ordinal) || key.StartsWith(CheckingKeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Matches a stored imported row to a purchase already counted. It looks first at older rows on the same account
    /// from the other source, an alert email for a CSV row or a CSV row for an alert, then at typed purchases. Each
    /// counted purchase stands in for at most one row, and one whose merchant name matches is picked first.
    /// </summary>
    private static bool Absorb(SqliteConnection connection, string account, ImportedTransaction row, string importKey, long ownId, bool merchantOnly, bool backfill)
    {
        var fromFile = IsFileKey(account, importKey);
        var imported = Candidates(connection, """
            SELECT i.id, i.title, i.due_date, i.import_key FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Expense' AND i.canceled = 0 AND i.import_key IS NOT NULL AND c.text = $extra AND i.id < $own
              AND i.amount_cents = $amount AND i.due_date BETWEEN $from AND $through
              AND NOT EXISTS (SELECT 1 FROM import_matches m WHERE m.item_id = i.id OR m.import_key = i.import_key)
            """, row, AlertMatchDays, $"{SpendingRow.ImportedPrefix}{account}", ownId)
            .Where(found => IsFileKey(account, found.Key!) != fromFile)
            .ToList();
        var typed = Candidates(connection, $"""
            SELECT i.id, i.title, {SpendDay}, NULL FROM items i JOIN captures c ON c.id = i.capture_id
            WHERE i.kind = 'Expense' AND i.import_key IS NULL AND i.canceled = 0 AND i.amount_cents = $amount AND i.id <> $own
              AND substr(c.text, 1, length($extra)) <> $extra
              AND {SpendDay} BETWEEN $from AND $through
              AND NOT EXISTS (SELECT 1 FROM import_matches m WHERE m.item_id = i.id)
            """, row, ImportMatchDays, SpendingRow.ImportedPrefix, ownId);

        foreach (var (candidates, isTyped) in new[] { (imported, false), (typed, true) })
        {
            var best = candidates
                .Where(found => !merchantOnly || SameMerchant(found.Title, row.Description))
                .OrderByDescending(found => SameMerchant(found.Title, row.Description))
                .ThenBy(found => Math.Abs(found.Date.DayNumber - row.Date.DayNumber))
                .ThenBy(found => found.Id)
                .Select(found => (long?)found.Id)
                .FirstOrDefault();
            if (best is not { } itemId)
            {
                continue;
            }

            using var record = connection.CreateCommand();
            record.CommandText = "INSERT INTO import_matches (import_key, item_id, typed, backfilled) VALUES ($key, $item, $typed, $backfilled)";
            record.Parameters.AddWithValue("$key", importKey);
            record.Parameters.AddWithValue("$item", itemId);
            record.Parameters.AddWithValue("$typed", isTyped ? 1 : 0);
            record.Parameters.AddWithValue("$backfilled", backfill ? 1 : 0);
            record.ExecuteNonQuery();
            return true;
        }

        return false;
    }

    /// <summary>Purchases at a row's amount near its date, by a query that takes one more value as $extra and the row's own id as $own.</summary>
    private static List<(long Id, string Title, DateOnly Date, string? Key)> Candidates(SqliteConnection connection, string sql, ImportedTransaction row, int days, string extra, long ownId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$amount", row.AmountCents);
        command.Parameters.AddWithValue("$from", row.Date.AddDays(-days).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$through", row.Date.AddDays(days).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$extra", extra);
        command.Parameters.AddWithValue("$own", ownId);

        var found = new List<(long, string, DateOnly, string?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            found.Add((reader.GetInt64(0), reader.GetString(1), DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return found;
    }

    /// <summary>Whether two names for one purchase share a word, such as "Target" and "TARGET #12".</summary>
    internal static bool SameMerchant(string one, string other) =>
        SearchIndex.Mentions(other, MerchantName.Clean(one)) || SearchIndex.Mentions(one, MerchantName.Clean(other));

    /// <summary>
    /// Runs once at startup on data from before matches were kept, where an alert email and the CSV row for the same
    /// purchase, or a typed purchase and its imported row, were both counted. It uses the same rules as an import,
    /// with the older imported row kept, and only adds matches, so <see cref="UndoMatchUpgrade"/> takes it back.
    /// </summary>
    private void BackfillImportMatches()
    {
        if (GetPreference(MatchUpgradeKey) is not null)
        {
            return;
        }

        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var rows = new List<(string Account, ImportedTransaction Row, string Key, long Id)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.id, c.text, i.due_date, i.title, i.amount_cents, i.import_key FROM items i JOIN captures c ON c.id = i.capture_id
                WHERE i.kind = 'Expense' AND i.canceled = 0 AND i.import_key IS NOT NULL AND i.due_date IS NOT NULL AND i.amount_cents IS NOT NULL
                  AND substr(c.text, 1, length($prefix)) = $prefix
                ORDER BY i.id
                """;
            command.Parameters.AddWithValue("$prefix", SpendingRow.ImportedPrefix);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(5);
                rows.Add((reader.GetString(1)[SpendingRow.ImportedPrefix.Length..],
                    new ImportedTransaction(DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetString(3), reader.GetInt64(4), key),
                    key,
                    reader.GetInt64(0)));
            }
        }

        foreach (var merchantOnly in new[] { true, false })
        {
            foreach (var (account, row, key, id) in rows)
            {
                if (!Matched(connection, key, id))
                {
                    Absorb(connection, account, row, key, id, merchantOnly, backfill: true);
                }
            }
        }

        using (var flag = connection.CreateCommand())
        {
            flag.CommandText = "INSERT INTO preferences (key, value) VALUES ($key, '1') ON CONFLICT (key) DO UPDATE SET value = excluded.value";
            flag.Parameters.AddWithValue("$key", MatchUpgradeKey);
            flag.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Whether a row is already a match, or already stands in for another row.</summary>
    private static bool Matched(SqliteConnection connection, string key, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM import_matches WHERE import_key = $key OR item_id = $id)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()! != 0;
    }

    /// <summary>Takes back the matches the startup upgrade found, so every row counts as it did before. It won't run again.</summary>
    public int UndoMatchUpgrade()
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM import_matches WHERE backfilled = 1";
        return command.ExecuteNonQuery();
    }
}
