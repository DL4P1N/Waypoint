using System.Text.RegularExpressions;

namespace Waypoint.Core.Discord;

/// <summary>
/// A note's Markdown laid out for Discord, which renders only part of it. Tables don't render, so each row becomes
/// one line with its cells separated by dots, and headings deeper than Discord's three levels become bold lines.
/// Checkboxes show as ☐ and ☑, the way lists do, and the editor's underline as Discord's.
/// </summary>
public static partial class DiscordNoteText
{
    public static string Format(string markdown)
    {
        var lines = new List<string>();
        var inCode = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                lines.Add(line);
                continue;
            }

            if (inCode)
            {
                lines.Add(line);
                continue;
            }

            if (TableRule().IsMatch(line))
            {
                continue;
            }

            // The editor's underline, which Discord writes as two underscores.
            line = Underline().Replace(line, "__$1__");

            // A checkbox reads the way a list's entries do.
            if (TaskItem().Match(line) is { Success: true } task)
            {
                lines.Add($"{task.Groups[1].Value}{(task.Groups[2].Value == " " ? "☐" : "☑")} {task.Groups[3].Value}");
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).Where(cell => cell.Length > 0);
                lines.Add(string.Join(" · ", cells));
                continue;
            }

            if (DeepHeading().Match(line) is { Success: true } heading)
            {
                lines.Add($"**{heading.Groups[1].Value.Trim()}**");
                continue;
            }

            lines.Add(line);
        }

        // Blank lines in a row read as gaps on a phone, so only one is kept.
        var text = string.Join("\n", lines);
        return ManyBlankLines().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^\s*#{4,6}\s+(.+)$")]
    private static partial Regex DeepHeading();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLines();

    [GeneratedRegex(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex TaskItem();

    [GeneratedRegex(@"<u>(.*?)</u>", RegexOptions.IgnoreCase)]
    private static partial Regex Underline();
}
