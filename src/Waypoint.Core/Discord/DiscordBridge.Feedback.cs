using System.Text.Json;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Discord;

/// <summary>
/// "This didn't help" on a card. The press itself is kept straight away, then a small form offers one optional
/// line. Closing the form keeps the click, and nothing is asked after.
/// </summary>
public sealed partial class DiscordBridge
{
    /// <summary>The button's ID. The card's feature and item reference follow, as feature|ref.</summary>
    public const string DidntHelpPrefix = "waypoint:didnt-help:";

    private const string DidntHelpForm = "waypoint:didnt-help-note:";
    private const string DidntHelpNote = "note";

    /// <summary>Keeps a press of This didn't help, given the card's feature and item reference, and returns its ID.</summary>
    public Func<string, string?, long>? FeedbackGiven { get; set; }

    /// <summary>The optional line from the form, given the ID the press was kept under.</summary>
    public Action<long, string?>? FeedbackNoted { get; set; }

    /// <summary>The small second row a card's This didn't help sits in, kept apart so it never pushes out a main button.</summary>
    private static object? FeedbackRow(DiscordCard card)
    {
        if (card.FeedbackFeature is not { } feature)
        {
            return null;
        }

        var id = Shorten(DidntHelpPrefix + feature + "|" + card.FeedbackRef, MaxCustomId);
        return new { type = ButtonRow, components = new object[] { new { type = Button, style = GrayButton, label = CardLabels.DidntHelp, custom_id = id } } };
    }

    private const int MaxCustomId = 100;

    private Answer DidntHelp(string button)
    {
        var about = button[DidntHelpPrefix.Length..];
        var split = about.IndexOf('|', StringComparison.Ordinal);
        var feature = split < 0 ? about : about[..split];
        var itemRef = split < 0 || split == about.Length - 1 ? null : about[(split + 1)..];
        long? id = null;
        try
        {
            id = FeedbackGiven?.Invoke(feature, itemRef);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
        }

        if (id is not { } kept)
        {
            return new Answer(AcknowledgeOnly);
        }

        return new Answer(ShowForm, new
        {
            custom_id = DidntHelpForm + kept.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title = CardLabels.DidntHelp,
            components = new[]
            {
                new
                {
                    type = ButtonRow,
                    components = new object[]
                    {
                        new { type = TextInput, custom_id = DidntHelpNote, label = "Anything to add? You can leave this blank", style = ShortText, required = false, max_length = Feedback.MaxNote },
                    },
                },
            },
        });
    }

    /// <summary>The form was sent, blank or not. The click is already kept, so this only adds the line.</summary>
    private Answer NoteFeedback(string form, JsonElement data)
    {
        if (long.TryParse(form[DidntHelpForm.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            && FormValues(data).GetValueOrDefault(DidntHelpNote) is { Length: > 0 } note)
        {
            try
            {
                FeedbackNoted?.Invoke(id, note);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
            }
        }

        return new Answer(AcknowledgeOnly);
    }

    private static bool IsFeedbackForm(string? form) => form?.StartsWith(DidntHelpForm, StringComparison.Ordinal) == true;
}
