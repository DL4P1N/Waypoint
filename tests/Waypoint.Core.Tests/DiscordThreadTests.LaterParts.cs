namespace Waypoint.Core.Tests;

/// <summary>
/// A part of a note's post someone deleted is sent again with the parts after it, which are taken away first. One of
/// those that can't be taken away stays known, so it's tried again rather than left in the post. No network.
/// </summary>
public sealed partial class DiscordThreadTests
{
    private static readonly string LongerReading = string.Join("\n\n", Enumerable.Range(1, 260).Select(line => $"Made up line {line} about a made up topic that runs on for a while."));

    [Fact]
    public async Task A_later_part_whose_delete_fails_stays_known_and_is_taken_away_on_a_later_round()
    {
        var id = _database.CreateNote("Made up longer reading", LongerReading, _now);
        var worker = await Started(lists: null);
        var post = PostFor(id);
        var kept = _database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message).ToList();
        Assert.True(kept.Count >= 3);

        // Someone deletes the second part, and the last one can't be deleted just now.
        lock (_discord.Posts)
        {
            post.Messages.RemoveAll(message => message.Id == kept[1]);
        }

        _discord.FailingDeletes.Add(kept[^1]);
        Assert.True(_database.SaveNote(id, "Made up longer reading", LongerReading.Replace("Made up line", "Made up changed line"), _now));
        await worker.SyncDiscordThreadsAsync();

        Assert.Contains(kept[^1], _database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message));
        Assert.Contains(post.Messages, message => message.Id == kept[^1]);

        _discord.FailingDeletes.Clear();
        await worker.SyncDiscordThreadsAsync();

        var parts = _database.FindDiscordThread(post.Id)!.Parts.Select(part => part.Message).ToList();
        Assert.DoesNotContain(kept[^1], parts);
        Assert.DoesNotContain(post.Messages, message => message.Id == kept[^1]);
        Assert.Equal(parts, post.Messages.Where(message => message.Bot).Select(message => message.Id));
        Assert.All(Descriptions(post), shown => Assert.DoesNotContain("Made up line", shown));
    }
}
