using Waypoint.Core.Remote;

namespace Waypoint.Core.Data;

/// <summary>Preferences are read through <see cref="IPreferences"/>, so a page can take them from the server instead.</summary>
public sealed partial class WaypointDatabase : IPreferences;
