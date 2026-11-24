using System.Text.RegularExpressions;
using Waypoint.Core.Data;

namespace Waypoint.Core.Notes;

/// <summary>
/// Puts back the marks the note editor wrongly kept as text. A space typed after "#", "-", "1.", "&gt;", or "[]" at
/// the start of a line was meant to make a heading, list item, quote, or checklist item, but for a while the editor
/// missed it and saved the marks escaped, such as "\# Week 9" or "1\. Mix". Only exactly what the editor would have
/// formatted changes: escapes anywhere else, indented lines, and anything inside a fenced code block stay as they are.
/// </summary>
public static partial class TypedMarkRepair
{
    /// <summary>
    /// Repairs the notes once, as <see cref="WaypointDatabase.RepairTypedNoteMarks"/> does, and never lets a problem
    /// stop Waypoint opening: anything that goes wrong is logged and the notes are left as they are.
    /// </summary>
    public static void RunOnce(WaypointDatabase database, Action<string, Exception?> log)
    {
        try
        {
            switch (database.RepairTypedNoteMarks(DateTime.Now))
            {
                case { Problem: { } problem, GaveUp: true }:
                    log("Notes saved with their typed headings and lists as text weren't repaired: no backup could be made, and it won't be tried again.", problem);
                    break;
                case { Problem: { } problem }:
                    log("Notes saved with their typed headings and lists as text weren't repaired yet: no backup could be made. It's tried again next time.", problem);
                    break;
                case { Notes: > 0 } done:
                    log($"Repaired {done.Lines} typed lines in {done.Notes} notes. The notes as they were are in {done.Backup}.", null);
                    break;
            }
        }
        catch (Exception exception)
        {
            log("Notes saved with their typed headings and lists as text couldn't be repaired.", exception);
        }
    }

    /// <summary>The note with its typed line starts made into marks again, and how many lines that changed.</summary>
    public static (string Body, int Lines) Repair(string body)
    {
        var lines = body.Split('\n');
        var output = new List<string>(lines.Length + 4);
        var changed = 0;
        char? fence = null;
        var fenceLength = 0;
        string? blankBefore = null;

        foreach (var line in lines)
        {
            var text = line.TrimEnd('\r');
            var ending = line.Length == text.Length ? "" : line[text.Length..];
            var inCode = fence is not null;
            if (Fence().Match(text) is { Success: true } found)
            {
                var marks = found.Groups[1].Value;
                if (fence is null)
                {
                    (fence, fenceLength) = (marks[0], marks.Length);
                }
                else if (marks[0] == fence && marks.Length >= fenceLength && found.Groups[2].Value.Trim().Length == 0)
                {
                    fence = null;
                }
            }

            var repaired = inCode || fence is not null ? text : RepairLine(text);

            // A list item or quote that was text has to end before a plain line, or Markdown reads that line as part of
            // it. The editor writes a blank line there, so the repair does too.
            if (blankBefore is { } blank && text.Trim().Length > 0 && !BlockStart().IsMatch(repaired))
            {
                output.Add(blank);
            }

            blankBefore = null;
            if (repaired != text)
            {
                changed++;
                if (ItemOrQuote().IsMatch(repaired))
                {
                    blankBefore = ending;
                }
            }

            output.Add(repaired + ending);
        }

        return changed == 0 ? (body, 0) : (string.Join('\n', output), changed);
    }

    private static string RepairLine(string text)
    {
        var repaired = Heading().Replace(text, "$1", 1);
        repaired = Bullet().Replace(repaired, "$1", 1);
        repaired = Number().Replace(repaired, "$1", 1);
        repaired = Quote().Replace(repaired, "$1", 1);

        // "[] " typed on a plain line makes a checklist item, and typed in a bullet makes it one.
        repaired = LineBox().Replace(repaired, "- [ ] ", 1);
        return ItemBox().Replace(repaired, "$1[ ] ", 1);
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex Fence();

    /// <summary>"#", "##", or "###" then a space: the headings typing makes.</summary>
    [GeneratedRegex(@"^\\(#{1,3} )(?=\S)")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\\([-*+] )(?=\S)")]
    private static partial Regex Bullet();

    /// <summary>"1." or "1)" then a space: the only numbers typing starts a list with.</summary>
    [GeneratedRegex(@"^(1)\\(?=[.)] \S)")]
    private static partial Regex Number();

    [GeneratedRegex(@"^\\(> )(?=\S)")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^\[ ?\] (?=\S)")]
    private static partial Regex LineBox();

    /// <summary>"[]" or "[ ]" typed at the start of a bullet, as the editor saved it then, at any depth.</summary>
    [GeneratedRegex(@"^( *[-*+] )(?:\[\]|\\\[ \]) (?=\S)")]
    private static partial Regex ItemBox();

    /// <summary>A list item or a quote, which a plain line right after would join.</summary>
    [GeneratedRegex(@"^ *(?:[-*+] |\d{1,9}[.)] |>)")]
    private static partial Regex ItemOrQuote();

    /// <summary>A heading, list item, quote, or code fence, which starts a block of its own rather than joining one.</summary>
    [GeneratedRegex(@"^ {0,3}(?:#{1,6}(?:\s|$)|[-*+]\s|\d{1,9}[.)]\s|>|`{3,}|~{3,})")]
    private static partial Regex BlockStart();
}
