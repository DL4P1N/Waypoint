using System.Text.RegularExpressions;

namespace Waypoint.Core.Notes;

/// <summary>
/// Puts back the marks the note editor wrongly kept as text. A space typed after "#", "-", "1.", "&gt;", or "[]" at
/// the start of a line was meant to make a heading, list item, quote, or checklist item, but for a while the editor
/// missed it and saved the marks escaped, such as "\# Week 9" or "1\. Mix". Only those line starts change: escapes
/// anywhere else in a line, and anything inside a fenced code block, stay exactly as they are.
/// </summary>
public static partial class TypedMarkRepair
{
    /// <summary>The note with its escaped line starts made into marks again, and how many lines that changed.</summary>
    public static (string Body, int Lines) Repair(string body)
    {
        var lines = body.Split('\n');
        var changed = 0;
        char? fence = null;
        var fenceLength = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var text = line.TrimEnd('\r');
            var ending = line[text.Length..];

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

                continue;
            }

            if (fence is not null)
            {
                continue;
            }

            var repaired = RepairLine(text);
            if (repaired != text)
            {
                lines[i] = repaired + ending;
                changed++;
            }
        }

        return changed == 0 ? (body, 0) : (string.Join('\n', lines), changed);
    }

    private static string RepairLine(string text)
    {
        var repaired = Heading().Replace(text, "$1$2", 1);
        repaired = Bullet().Replace(repaired, "$1$2", 1);
        repaired = Number().Replace(repaired, "$1$2$3", 1);
        repaired = Quote().Replace(repaired, "$1$2", 1);

        // "[] " typed on a plain line was meant as a checklist item, and in a list item it was meant to make it one.
        repaired = LineBox().Replace(repaired, "$1- [ ] ", 1);
        return ItemBox().Replace(repaired, "$1[ ] ", 1);
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^( {0,3})\\(#{1,6} )")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^( {0,3})\\([-*+] )")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^( {0,3})(\d{1,9})\\([.)] )")]
    private static partial Regex Number();

    [GeneratedRegex(@"^( {0,3})\\(> )")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^( {0,3})\[ ?\] (?=\S)")]
    private static partial Regex LineBox();

    [GeneratedRegex(@"^( {0,3}[-*+] )(?:\[\]|\\\[ \]) (?=\S)")]
    private static partial Regex ItemBox();
}
