using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>
/// Two writers on the same thing at the same moment, the way the worker (Discord, email, sorting) and a click in the
/// window or a press on the phone can be. Each writer has its own WaypointDatabase over the same file, as the
/// worker and the window do, and they're let go together. Nothing either wrote may be lost, nothing may be counted
/// twice, and no write may fail with "database is locked" while SQLite's busy wait should cover it.
/// </summary>
public sealed class ConcurrentWritersTests : IDisposable
{
    private const int Rounds = 30;
    private static readonly DateOnly Day = new(2030, 3, 12);
    private static readonly DateTime Now = new(2030, 3, 12, 9, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-writers-{Guid.NewGuid():N}");
    private readonly string _path;
    private readonly WaypointDatabase _worker;
    private readonly WaypointDatabase _window;

    public ConcurrentWritersTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "waypoint.db");
        _worker = new WaypointDatabase(_path);
        _window = new WaypointDatabase(_path);
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

    /// <summary>Runs each writer on its own thread, all let go at once, and returns anything any of them threw.</summary>
    private static List<Exception> Run(params Action[] writers)
    {
        var failures = new List<Exception>();
        using var start = new Barrier(writers.Length);
        var threads = writers.Select(writer => new Thread(() =>
        {
            start.SignalAndWait();
            try
            {
                writer();
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        })).ToList();
        threads.ForEach(thread => thread.Start());
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A writer was still going after a minute.");
        }

        return failures;
    }

    private static void NoneFailed(List<Exception> failures) =>
        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Select(failure => failure.ToString())));

    private long Scalar(string sql)
    {
        using var connection = _worker.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Entries_added_to_one_list_from_Discord_and_the_window_at_once_are_all_kept_once()
    {
        _window.CreateList("Made up camping", Now);
        var reading = true;

        var failures = Run(
            () =>
            {
                for (var n = 0; n < Rounds; n++)
                {
                    _worker.AddListEntry("Made up camping", $"From the phone {n}", Now);
                    _worker.AddListEntry("Made up camping", "Tent", Now);
                }
            },
            () =>
            {
                for (var n = 0; n < Rounds; n++)
                {
                    _window.AddListEntry("Made up camping", $"From the window {n}", Now);
                    _window.AddListEntry("Made up camping", "Tent", Now);
                }
            },
            () =>
            {
                // A page refreshing all the while, as the window and the Discord views do.
                while (Volatile.Read(ref reading))
                {
                    _window.GetListOverview(includeEmptyShopping: true);
                    if (_window.GetListEntries("Made up camping").Count >= (2 * Rounds) + 1)
                    {
                        Volatile.Write(ref reading, false);
                    }
                }
            });

        NoneFailed(failures);
        var entries = _window.GetListEntries("Made up camping").Select(entry => entry.Text).ToList();
        Assert.Equal((2 * Rounds) + 1, entries.Count);
        Assert.Single(entries, text => text == "Tent");
        Assert.Equal(entries.Count, entries.Distinct().Count());
    }

    [Fact]
    public void Shopping_added_from_Discord_and_the_window_at_once_is_all_kept_once()
    {
        var failures = Run(
            () =>
            {
                for (var n = 0; n < Rounds; n++)
                {
                    _worker.AddListEntry(WaypointDatabase.ShoppingList, $"Made up fruit {n}", Now);
                    _worker.AddListEntry(WaypointDatabase.ShoppingList, "Oats", Now);
                }
            },
            () =>
            {
                for (var n = 0; n < Rounds; n++)
                {
                    _window.AddListEntry(WaypointDatabase.ShoppingList, $"Made up bread {n}", Now);
                    _window.AddListEntry(WaypointDatabase.ShoppingList, "Oats", Now);
                }
            });

        NoneFailed(failures);
        var entries = _window.GetListEntries(WaypointDatabase.ShoppingList).Select(entry => entry.Text).ToList();
        Assert.Equal((2 * Rounds) + 1, entries.Count);
        Assert.Single(entries, text => text == "Oats");
    }

    [Fact]
    public void Paragraphs_added_to_one_note_from_two_posts_at_once_are_each_kept_once()
    {
        var id = _window.CreateNote("Made up trip", "Start", Now);

        void Append(WaypointDatabase database, string line)
        {
            // As the worker adds to a note from a post: read, add, and save only if nothing changed meanwhile.
            while (true)
            {
                var note = database.GetNote(id)!;
                if (database.SaveNoteIfUnchanged(id, note.Title, note.Body + "\n\n" + line, note.Body, Now))
                {
                    return;
                }
            }
        }

        var failures = Run(
            () =>
            {
                for (var n = 0; n < Rounds; n++)
                {
                    Append(_worker, $"From the notes post {n}");
                }
            },
            () =>
            {
                for (var n = 0; n < Rounds; n++)
                {
                    Append(_window, $"From the window {n}");
                }
            });

        NoneFailed(failures);
        var lines = _window.GetNote(id)!.Body.Split("\n\n");
        Assert.Equal((2 * Rounds) + 1, lines.Length);
        Assert.Equal(lines.Length, lines.Distinct().Count());
        for (var n = 0; n < Rounds; n++)
        {
            Assert.Contains($"From the notes post {n}", lines);
            Assert.Contains($"From the window {n}", lines);
        }
    }

    [Fact]
    public void A_task_checked_off_in_the_window_while_the_worker_renames_it_keeps_both()
    {
        var failures = new List<Exception>();
        for (var round = 0; round < Rounds; round++)
        {
            var added = _window.AddByHand(new HandAddedItem(ItemKind.Task, $"Made up errand {round}", Day), Now).Item!;

            failures.AddRange(Run(
                () => _worker.ChangeItem(new ItemChange(ItemSource.Capture, added.Id, ChangeAction.Update, Title: $"Made up errand {round}, renamed"), Now),
                () => _window.SetDone(added, true)));

            var item = _window.FindItems([ItemRef.Format(added)]).Single();
            Assert.Equal($"Made up errand {round}, renamed", item.Title);
            Assert.True(item.Done);
        }

        NoneFailed(failures);
        Assert.Equal(Rounds, Scalar("SELECT COUNT(*) FROM item_history"));
    }

    [Fact]
    public void A_repayment_matched_from_the_phone_and_the_window_at_once_is_counted_once()
    {
        for (var round = 0; round < Rounds; round++)
        {
            _window.ImportTransactions("Made Up Card", [new ImportedTransaction(Day.AddDays(round), $"MADE UP BAKERY {round}", 3000)]);
            var purchase = Scalar($"SELECT id FROM items WHERE title LIKE '%BAKERY {round}' COLLATE NOCASE");
            Assert.NotNull(_window.AddRepayment($"made-up-payment-{round}", new PaymentAppPayment("A Made Up Friend", 1500, null), Day));
            var replies = new List<string>();

            NoneFailed(Run(
                () =>
                {
                    var reply = _worker.MatchRepaymentTo($"made-up-payment-{round}", purchase).Text;
                    lock (replies)
                    {
                        replies.Add(reply);
                    }
                },
                () =>
                {
                    var reply = _window.MatchRepaymentTo($"made-up-payment-{round}", purchase).Text;
                    lock (replies)
                    {
                        replies.Add(reply);
                    }
                }));

            Assert.Equal(1500, Scalar($"SELECT reimbursed_cents FROM items WHERE id = {purchase}"));
            Assert.Single(replies, reply => reply.StartsWith("Marked", StringComparison.Ordinal));
            Assert.Single(replies, reply => reply == "That repayment is already settled");
        }
    }

    [Fact]
    public async Task Captures_typed_while_another_copy_sorts_them_are_each_sorted_once()
    {
        var sorter = new OneTask();
        var service = new CaptureService(_worker, sorter);
        var typed = new List<long>();

        var sorting = Task.Run(async () =>
        {
            for (var n = 0; n < Rounds; n++)
            {
                await service.ProcessPendingAsync();
            }
        });
        var typing = Task.Run(() =>
        {
            for (var n = 0; n < Rounds; n++)
            {
                typed.Add(_window.AddCapture($"made up chore {n}"));
            }
        });
        await Task.WhenAll(sorting, typing);
        await service.ProcessPendingAsync();

        Assert.Null(service.LastError);
        Assert.Empty(_window.GetPendingCaptures());
        Assert.Equal(Rounds, Scalar("SELECT COUNT(*) FROM items WHERE kind = 'Task'"));
        Assert.Equal(Rounds, Scalar("SELECT COUNT(DISTINCT capture_id) FROM items WHERE kind = 'Task'"));
        Assert.Equal(Rounds, sorter.Sorted);
    }

    /// <summary>Makes each capture a task named after it, and counts how many it was asked to sort.</summary>
    private sealed class OneTask : ICaptureSorter
    {
        private int _sorted;

        public int Sorted => Volatile.Read(ref _sorted);

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sorted);
            return Task.FromResult<CaptureResult?>(new CaptureResult([new SortedItem(ItemKind.Task, text, null, null)]));
        }
    }
}
