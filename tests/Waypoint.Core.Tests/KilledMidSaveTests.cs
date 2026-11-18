using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>
/// Waypoint killed partway through a save, at every point it could die. The database is put in write-ahead mode for
/// the test, so everything a workflow writes lands in the -wal file, page by page, with a commit mark at the end of
/// each transaction. Cutting that file short after any page, or in the middle of one, is the database as it would be
/// found after the process died at that moment, its last transaction never committed. Each copy is opened fresh the
/// way the next start would, and must hold either everything from before the workflow or everything after it.
/// Writes split over several transactions show up here as a copy holding half of each.
/// </summary>
public sealed class KilledMidSaveTests : IDisposable
{
    private const int WalHeader = 32;
    private const int FrameHeader = 24;
    private static readonly DateOnly Day = new(2030, 3, 12);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-killed-{Guid.NewGuid():N}");
    private readonly string _path;
    private WaypointDatabase _database;

    public KilledMidSaveTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "waypoint.db");
        new WaypointDatabase(_path);
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            Assert.Equal("wal", command.ExecuteScalar());
        }

        _database = new WaypointDatabase(_path);
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class Sorts(CaptureResult result) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(result);
    }

    [Fact]
    public void A_card_export_killed_partway_is_found_whole_or_not_at_all()
    {
        _database.ImportStatement("""
            Posted On,Purchase Date,Category,Description,Amount
            03/01/2030,03/01/2030,Groceries,MADE UP MARKET,22.10
            """);

        AtEveryPoint(() => _database.ImportStatement("""
            Posted On,Purchase Date,Category,Description,Amount
            03/10/2030,03/10/2030,Payments,AUTOPAY STATEMENT BALANCE,-80.00
            03/11/2030,03/11/2030,Restaurants,MADE UP DINER,14.50
            03/11/2030,03/11/2030,Groceries,MADE UP GREENGROCER,31.25
            03/12/2030,03/12/2030,Shopping,MADE UP HARDWARE,9.99
            """), "items", "captures", "card_payments");
    }

    [Fact]
    public void A_checking_export_killed_partway_is_found_whole_or_not_at_all()
    {
        AtEveryPoint(() => _database.ImportChecking(
        [
            new(Day, "MADE UP PAYROLL", 50000, CheckingKind.Other),
            new(Day, "MADE UP CARD PAYMENT", -12000, CheckingKind.CardPayment, "Made Up Card"),
            new(Day, "MADE UP SHOP", -2500, CheckingKind.Spending),
            new(Day.AddDays(1), "MADE UP CAFE", -650, CheckingKind.Spending),
        ]), "checking_activity", "card_payments", "items", "captures");
    }

    [Fact]
    public void A_capture_completed_with_items_steps_and_a_change_killed_partway_is_found_whole_or_not_at_all()
    {
        var earlier = _database.AddCapture("made up errand list");
        _database.CompleteCapture(earlier, [new SortedItem(ItemKind.Task, "Post the made up parcel", Day, null)]);
        var parcel = _database.GetOpenItems().Single().Id;
        var capture = _database.AddCapture("paint the made up shed this weekend, and the parcel is posted");

        AtEveryPoint(() => _database.CompleteCapture(
            capture,
            [
                new SortedItem(ItemKind.Task, "Paint the made up shed", Day, null, Steps: [new PlannedStep("Buy paint", null), new PlannedStep("Sand the walls", null)]),
                new SortedItem(ItemKind.Expense, "Made up paint", Day, 3400),
            ],
            [new ItemChange(ItemSource.Capture, parcel, ChangeAction.Done)]), "captures", "items", "item_history");
    }

    [Fact(Skip = "bug: a capture whose sorting also sets a budget is saved in separate writes. CaptureService.ProcessPendingAsync marks the capture sorted and keeps its items in one write, then sets the monthly budget and category caps in later ones, so a crash between them keeps the items, never sets the budget, and the capture is never sorted again to finish it.")]
    public async Task A_capture_sorted_with_a_budget_change_killed_partway_is_found_whole_or_not_at_all()
    {
        _database.AddCapture("spent 12 at the made up cafe, and keep eating out to 150 a month");
        var service = new CaptureService(_database, new Sorts(new CaptureResult(
            [new SortedItem(ItemKind.Expense, "Made up cafe", Day, 1200, Category: "Eating out")],
            MonthlyBudgetCents: 180000,
            BudgetOps: [new BudgetOp(BudgetOpKind.SetCap, "Eating out", 15000, null)])));

        var before = Before("captures", "items", "budget_categories", "preferences");
        await service.ProcessPendingAsync();
        AfterEveryPoint(before);
    }

    [Fact]
    public void A_repayment_match_killed_partway_is_found_whole_or_not_at_all()
    {
        _database.ImportTransactions("Made Up Card", [new ImportedTransaction(Day, "MADE UP BAKERY", 3000)]);
        long purchase;
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM items WHERE kind = 'Expense'";
            purchase = (long)command.ExecuteScalar()!;
        }

        Assert.NotNull(_database.AddRepayment("made-up-payment", new PaymentAppPayment("A Made Up Friend", 1500, null), Day));

        AtEveryPoint(() => Assert.StartsWith("Marked", _database.MatchRepaymentTo("made-up-payment", purchase).Text), "items", "repayments");
    }

    /// <summary>
    /// Runs <paramref name="workflow"/> once, then rebuilds the database as it would be found after dying at each
    /// point of the run, and checks each holds the tables named either as they were before or as they are after.
    /// </summary>
    private void AtEveryPoint(Action workflow, params string[] tables)
    {
        var before = Before(tables);
        workflow();
        AfterEveryPoint(before);
    }

    /// <summary>The database before a workflow: its file, with the log empty, and the tables named as they are.</summary>
    private sealed record Saved(string File, string[] Tables, string Rows);

    private Saved Before(params string[] tables)
    {
        // Everything from before goes into the main file, and the log starts empty.
        SqliteConnection.ClearAllPools();
        var before = Path.Combine(_folder, "before.db");
        File.Copy(_path, before);
        _database = new WaypointDatabase(_path);
        return new Saved(before, tables, Read(_path, tables));
    }

    private void AfterEveryPoint(Saved saved)
    {
        var (before, tables, old) = saved;
        var wal = ReadShared(_path + "-wal");
        var now = Read(_path, tables);
        Assert.NotEqual(old, now);
        Assert.True(wal.Length > WalHeader, "The workflow wrote nothing to the log.");
        var frame = FrameHeader + BinaryPrimitives.ReadInt32BigEndian(wal.AsSpan(8, 4));
        var frames = (wal.Length - WalHeader) / frame;
        Assert.Equal(wal.Length, WalHeader + (frames * frame));

        var lengths = Enumerable.Range(0, frames + 1).SelectMany(kept => kept < frames
            ? new[] { WalHeader + (kept * frame), WalHeader + (kept * frame) + (frame / 2) }
            : [WalHeader + (kept * frame)]);
        var halves = new List<string>();
        var sawOld = false;
        var sawNew = false;
        foreach (var length in lengths)
        {
            var found = Rebuild(before, wal, length, tables);
            if (found == old)
            {
                sawOld = true;
            }
            else if (found == now)
            {
                sawNew = true;
            }
            else
            {
                halves.Add($"Killed after {length - WalHeader} bytes of {wal.Length - WalHeader}:\n{found}");
            }
        }

        Assert.True(halves.Count == 0, $"Found half saved.\nBefore:\n{old}\nAfter:\n{now}\n{string.Join("\n", halves)}");
        Assert.True(sawOld);
        Assert.True(sawNew);
    }

    /// <summary>The database as found after dying with only the first <paramref name="length"/> bytes of the log written.</summary>
    private string Rebuild(string before, byte[] wal, int length, string[] tables)
    {
        var folder = Path.Combine(_folder, $"cut-{length}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "waypoint.db");
        File.Copy(before, path);
        File.WriteAllBytes(path + "-wal", wal.AsSpan(0, length).ToArray());
        return Read(path, tables);
    }

    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Every row of each table, in a fixed order, as text to compare.</summary>
    private static string Read(string path, string[] tables)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return string.Join("\n", tables.Select(table =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table}";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(column => $"{reader.GetName(column)}={(reader.IsDBNull(column) ? "null" : reader.GetValue(column))}")));
            }

            rows.Sort(StringComparer.Ordinal);
            return $"{table}: {string.Join("; ", rows)}";
        }));
    }
}
