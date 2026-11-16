using System.Text.Json;
using System.Text.Json.Nodes;

namespace Waypoint.Core.Schedule;

/// <summary>
/// Moves state saved before the two wind-down steps had slots of their own, when it was kept under each step's
/// name. A saved key goes to the slot whose configured name it matches, ignoring case. A key that matches no
/// name, or matches both because the two names are the same, is left as it is, so nothing is ever misfiled.
/// Moved keys take the slot's fixed key, so renaming a step later never moves anything again.
/// </summary>
public static class WindDownUpgrade
{
    private static readonly HashSet<string> ChoiceKeys =
        typeof(BedtimeChoices).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stage a key saved under a step's name now goes by, or null when it names neither step, or both.</summary>
    public static BedtimeTask? SlotFor(string key, BedtimeSettings settings)
    {
        var evening = key.Equals(settings.EveningStepLabel(), StringComparison.OrdinalIgnoreCase);
        var windDown = key.Equals(settings.WindDownStepLabel(), StringComparison.OrdinalIgnoreCase);
        return evening == windDown ? null : evening ? BedtimeTask.EveningStep : BedtimeTask.WindDownStep;
    }

    /// <summary>
    /// The Settings page's saved choices with any on and off switch kept under a step's name moved to that step's
    /// switch, or null when there's nothing to move. A switch already saved under the slot's key wins.
    /// </summary>
    public static string? UpgradeChoices(string? json, BedtimeSettings file)
    {
        if (json is null || Parse(json) is not { } root)
        {
            return null;
        }

        var settings = BedtimeChoices.Parse(json).ApplyTo(file);
        var moved = false;
        foreach (var key in root.Select(property => property.Key).ToList())
        {
            if (ChoiceKeys.Contains(key)
                || root[key] is { } value && value.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                || SlotFor(key, settings) is not { } slot)
            {
                continue;
            }

            var current = slot == BedtimeTask.EveningStep ? nameof(BedtimeChoices.EveningStep) : nameof(BedtimeChoices.WindDownStep);
            var switched = root[key];
            root.Remove(key);
            if (!root.ContainsKey(current))
            {
                root[current] = switched;
            }

            moved = true;
        }

        return moved ? root.ToJsonString() : null;
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
