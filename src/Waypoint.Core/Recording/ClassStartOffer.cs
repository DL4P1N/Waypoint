using Waypoint.Core.Schedule;

namespace Waypoint.Core.Recording;

/// <summary>
/// The optional question at the start of a class: record it or not. It's off unless the user turns it on in
/// Settings, it only ever asks, and each class is asked about once. Nothing is recorded without a yes.
/// </summary>
public sealed class ClassStartOffer(ClassSchedule? schedule)
{
    public const string Preference = "offer_record_at_class_start";

    /// <summary>Asked in the first few minutes of class. A PC woken later in the class isn't asked.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly HashSet<ClassOccurrence> _offered = [];

    public static bool IsOn(string? preference) => preference == "yes";

    /// <summary>The class to ask about now, or null. A class is only returned once.</summary>
    public ClassOccurrence? Check(DateTime now, bool turnedOn, bool recording)
    {
        if (!turnedOn || recording || schedule is null)
        {
            return null;
        }

        var due = schedule.GetClasses(DateOnly.FromDateTime(now))
            .FirstOrDefault(occurrence => now >= occurrence.Start && now < occurrence.Start + Window && !_offered.Contains(occurrence));
        if (due is not null)
        {
            _offered.Add(due);
        }

        return due;
    }
}
