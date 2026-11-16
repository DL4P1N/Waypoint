using System;
using System.Collections.Generic;

namespace Waypoint.App;

/// <summary>
/// A note's undo and redo, carried between the formatted note and Show Markdown so switching keeps them. Each entry
/// is the note's whole text at that step. <see cref="Undo"/> is oldest first, so its last is what Ctrl+Z goes back
/// to; <see cref="Redo"/>'s last is what Ctrl+Y goes to next. <see cref="Original"/> is the note exactly as it was
/// opened and <see cref="OriginalJoined"/> how it reads in these texts, so undoing every change gives it back as it was.
/// </summary>
internal sealed record EditHistory(IReadOnlyList<string> Undo, IReadOnlyList<string> Redo, string Original, string OriginalJoined)
{
    /// <summary>Where the caret goes in <paramref name="to"/> after a step from <paramref name="from"/>: the end of what changed.</summary>
    public static int ChangedEnd(string from, string to)
    {
        var limit = Math.Min(from.Length, to.Length);
        var start = 0;
        while (start < limit && from[start] == to[start])
        {
            start++;
        }

        var end = 0;
        while (end < limit - start && from[from.Length - 1 - end] == to[to.Length - 1 - end])
        {
            end++;
        }

        return to.Length - end;
    }
}
