using System.Globalization;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>The buttons on posts: Edit on a note's part, with its form and how the edit is saved, and Start over on a list's post.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>The Edit button on a note's part, and Start over on a list's post. Null when it isn't one of them.</summary>
    private ButtonResult? OnThreadAction(string action)
    {
        if (action.StartsWith(NoteEdit, StringComparison.Ordinal))
        {
            return EditPartForm(action[NoteEdit.Length..]);
        }

        if (action.StartsWith(ListStart, StringComparison.Ordinal))
        {
            if (ListForTarget(action[ListStart.Length..]) is not { } list)
            {
                return new ButtonResult(Outcome: "That list is gone now.");
            }

            _database.StartList(list.Name);
            var started = _database.GetListOverview(includeEmptyShopping: true).First(found => found.Name == list.Name);
            return new ButtonResult(ListPostCard(started));
        }

        return null;
    }

    private static bool TryReadPart(string rest, out long noteId, out int part, out string hash)
    {
        noteId = 0;
        part = 0;
        hash = "";
        var pieces = rest.Split(':');
        if (pieces.Length != 3
            || !long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out noteId)
            || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out part))
        {
            return false;
        }

        hash = pieces[2];
        return true;
    }

    /// <summary>
    /// The form for one part of a note, filled in with that part's Markdown as the note has it now, carrying a hash
    /// of it so saving can tell whether it changed meanwhile. A part too long for a form box says to use the PC.
    /// </summary>
    private ButtonResult EditPartForm(string rest)
    {
        static ButtonResult Private(string title, string text) =>
            new(DiscordText.Notice(title, text, CardTone.Attention) with { Private = true });

        if (!TryReadPart(rest, out var noteId, out var part, out _) || _database.GetNote(noteId) is not { } note)
        {
            return Private("That note is gone", "It was deleted on the PC.");
        }

        // A part is at most 3000 characters of Markdown, so it always fits the 4000 a form box holds, with room to add.
        var chunks = SplitMarkdown(note.Body, PartMarkdown);
        part = Math.Clamp(part, 0, chunks.Count - 1);
        var chunk = chunks[part];
        var shown = chunk.Replace("\r\n", "\n").TrimEnd('\n');
        var id = $"{NoteEdit}{noteId.ToString(CultureInfo.InvariantCulture)}:{part.ToString(CultureInfo.InvariantCulture)}:{Hash(chunk)}";
        if (_formsOpened.Count >= MaxOpenForms)
        {
            _formsOpened.Clear();
        }

        _formsOpened[id] = shown;
        var title = chunks.Count == 1 ? "Edit note" : $"Edit page {(part + 1).ToString(CultureInfo.InvariantCulture)} of {chunks.Count.ToString(CultureInfo.InvariantCulture)}";
        return new ButtonResult
        {
            Form = new CardForm(id, title, [new FormBox("text", "Note text, in Markdown", Long: true, Required: true, Value: shown, MaxLength: MaxEditText)]),
        };
    }

    /// <summary>
    /// A part's form was saved. If that part is still what the form was filled with, it's replaced with what was
    /// typed and nothing else changes. If it changed meanwhile, nothing is replaced: what was typed goes at the end
    /// of the note under a "From Discord" line, and the reply says so. It never waits for a round of updates, since
    /// each save checks the note didn't change meanwhile, and if it can't be saved, the reply holds what was typed.
    /// </summary>
    private CommandReply SavePartEdit(string rest, IReadOnlyDictionary<string, string> values)
    {
        static CommandReply Reply(string title, string text, CardTone tone) => new(DiscordText.Notice(title, text, tone), true);

        var typed = values.GetValueOrDefault("text") ?? "";
        var opened = _formsOpened.TryRemove(WaypointWorker.NoteEdit + rest, out var was) ? was : null;

        // Nothing typed is ever lost: when it can't be saved, the private reply holds it, ready to copy.
        CommandReply NotSaved(string why) => new(new DiscordCard("Not saved", typed.Trim().Length == 0 ? DiscordText.Escape(why) : $"{DiscordText.Escape(why)} Here's what you wrote, to copy:\n```\n{DiscordText.Shorten(typed.Replace("```", "`\u200b``"), MaxDescription - why.Length - 80)}\n```", [], CardTone.Attention), true);

        if (!TryReadPart(rest, out var noteId, out var part, out var hash) || _database.GetNote(noteId) is not { } note)
        {
            return NotSaved("That note was deleted on the PC. Bring it back from History there, then try again.");
        }

        if (typed.Trim().Length == 0)
        {
            return Reply("Not saved", "The text was empty, so the note stayed as it was.", CardTone.Attention);
        }

        var chunks = SplitMarkdown(note.Body, PartMarkdown).ToList();
        if (part < chunks.Count && Hash(chunks[part]) == hash)
        {
            chunks[part] = Replacement(chunks[part], typed);
            if (_database.SaveNoteIfUnchanged(noteId, note.Title, string.Concat(chunks), note.Body, _clock()))
            {
                _host.Changed();
                ViewsChanged();
                return Reply("Saved", "", CardTone.Finished);
            }
        }

        // The PC's change stays as it is. Only the lines changed or added in the form go at the end, so the rest
        // doesn't show up twice.
        var added = opened is null ? typed.Replace("\r\n", "\n").Trim('\n') : ChangedLines(opened, typed);
        if (added.Trim().Length == 0)
        {
            return NotSaved("That part changed on the PC since the form was opened, so nothing was replaced, and the form only took lines away. Make that change on the PC.");
        }

        if (!AppendToNote(noteId, FromDiscord + "\n\n" + added))
        {
            return NotSaved("The note kept changing on the PC, so it wasn't changed. Try again in a moment.");
        }

        _host.Changed();
        ViewsChanged();
        return Reply("Added at the end instead", $"That part changed since the form was opened, so nothing was replaced. The lines you changed or added were added at the end of the note under a \"{FromDiscord}\" line.", CardTone.Attention);
    }

    /// <summary>The lines in what was typed that weren't in the form as it opened, in order, found by the longest run they share.</summary>
    public static string ChangedLines(string opened, string typed)
    {
        var before = opened.Replace("\r\n", "\n").Split('\n');
        var after = typed.Replace("\r\n", "\n").Split('\n');
        var shared = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                shared[i, j] = before[i] == after[j] ? shared[i + 1, j + 1] + 1 : Math.Max(shared[i + 1, j], shared[i, j + 1]);
            }
        }

        // Changed lines that sit apart in the form, with unchanged ones between, stay apart as their own paragraphs.
        var changed = new List<string>();
        int x = 0, y = 0, last = -2;
        while (y < after.Length)
        {
            if (x < before.Length && before[x] == after[y])
            {
                x++;
                y++;
            }
            else if (x < before.Length && shared[x + 1, y] >= shared[x, y + 1])
            {
                x++;
            }
            else
            {
                if (changed.Count > 0 && last != y - 1 && changed[^1].Trim().Length > 0)
                {
                    changed.Add("");
                }

                changed.Add(after[y]);
                last = y;
                y++;
            }
        }

        return System.Text.RegularExpressions.Regex.Replace(string.Join("\n", changed), @"\n{3,}", "\n\n").Trim('\n');
    }

    /// <summary>The typed text in place of a part, with the part's own line endings and the line breaks it ended with.</summary>
    private static string Replacement(string chunk, string typed)
    {
        var newline = chunk.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailing = chunk[chunk.TrimEnd('\r', '\n').Length..];
        return typed.Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", newline) + trailing;
    }
}
