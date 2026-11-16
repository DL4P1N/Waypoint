namespace Waypoint.Core.Discord;

/// <summary>
/// One box on a pop-up form. Long boxes take several lines. Value fills it in to start with, and nothing is required
/// unless Required says so. Discord shows at most 45 characters of a label.
/// </summary>
public sealed record FormBox(string Id, string Label, bool Long = false, bool Required = false, string? Value = null, int MaxLength = 300, string? Placeholder = null);

/// <summary>
/// A pop-up form on the phone (a Discord modal), opened by a command or a button. Id comes back with what was typed,
/// so it can carry what the form is for. Discord allows at most five boxes and a 45-character title.
/// </summary>
public sealed record CardForm(string Id, string Title, IReadOnlyList<FormBox> Boxes)
{
    public const int MaxBoxes = 5;
    public const int MaxTitle = 45;
    public const int MaxLabel = 45;
}
