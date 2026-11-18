using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>A post's name and tags: the forum's tags kept matching the note folders, and when a renamed post's title has settled.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>What a post was last named and tagged, as a hash of each, so a change to one is told from the other.</summary>
    private static string Meta(string name, IReadOnlyList<string> tags) => Hash(name) + ":" + Hash(TagList(tags));

    private static string TagList(IReadOnlyList<string> tags) => string.Join(",", tags);

    private static (string? Name, string? Tags) SplitMeta(string? meta) =>
        meta?.Split(':') is [var name, var tags] ? (name, tags) : (null, null);

    /// <summary>Whether a post's new title has stayed the same long enough to rename the post to it.</summary>
    private bool TitleSettled(string thread, string name)
    {
        if (_titlesWaiting.TryGetValue(thread, out var waiting) && waiting.Name == name)
        {
            if (_clock() - waiting.Since < RenameSettle)
            {
                return false;
            }

            _titlesWaiting.TryRemove(thread, out _);
            return true;
        }

        _titlesWaiting[thread] = (name, _clock());
        return false;
    }

    /// <summary>
    /// Folder IDs and the forum tag each one has, made to match the folders when they've changed. Null when the
    /// forum can't be read and nothing is known yet, so nothing about tags is changed until it can be.
    /// </summary>
    private async Task<Dictionary<long, string>?> ForumTagsAsync(DiscordBridge discord, string forum)
    {
        var (names, untagged) = FolderTagNames();
        var hash = Hash(forum + "|" + string.Join("|", names.Select(pair => $"{pair.Key}={pair.Value}")));
        if (_forumTags is { } cached && cached.Hash == hash && _clock() - cached.At < (cached.Worked ? TagsCheckEvery : TagsRetryAfter))
        {
            return cached.Tags;
        }

        ForumNotice = untagged > 0
            ? $"The notes forum can only have {MaxTags} tags, so notes in {untagged} {(untagged == 1 ? "folder have" : "folders have")} no tag. Their posts still show the notes."
            : null;
        if (await discord.GetForumAsync(forum) is not { } info)
        {
            return _forumTags?.Tags;
        }

        _guildId ??= info.GuildId;
        var have = info.Tags.GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var wantedNames = names.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<ForumTag> now = info.Tags;
        var worked = true;
        var same = wantedNames.Count == info.Tags.Count && wantedNames.All(have.ContainsKey);
        if (!same)
        {
            var list = wantedNames.Select(name => (have.TryGetValue(name, out var id) ? id : null, name)).ToList();
            if (await discord.SetForumTagsAsync(forum, list) is { } set)
            {
                now = set;
            }
            else
            {
                worked = false;
            }
        }

        var byName = now.GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var tags = names.Where(pair => byName.ContainsKey(pair.Value)).ToDictionary(pair => pair.Key, pair => byName[pair.Value]);
        _forumTags = (hash, tags, _clock(), worked);
        return tags;
    }

    /// <summary>
    /// The tag name for each folder, in folder order: its own name at the top, "Parent / Child" inside another when
    /// that fits in 20 characters, else its own name. Only the first 20 get one; the count of the rest comes back too.
    /// </summary>
    private (Dictionary<long, string> Names, int Untagged) FolderTagNames()
    {
        var names = new Dictionary<long, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var untagged = 0;

        void Add(IEnumerable<NoteFolder> folders, string? parent)
        {
            foreach (var folder in folders)
            {
                var nested = parent is null ? folder.Name : $"{parent} / {folder.Name}";
                var name = (nested.Length <= MaxTagName ? nested : folder.Name).Trim();
                name = name.Length > MaxTagName ? name[..MaxTagName].TrimEnd() : name;
                if (names.Count < MaxTags && name.Length > 0 && used.Add(name))
                {
                    names[folder.Id] = name;
                }
                else
                {
                    untagged++;
                }

                Add(folder.Children, folder.Name);
            }
        }

        Add(_database.GetNoteFolders(), null);
        return (names, untagged);
    }
}
