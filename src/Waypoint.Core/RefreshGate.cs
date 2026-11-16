namespace Waypoint.Core;

/// <summary>
/// Decides when a page may rebuild its rows. The app refreshes every minute, so an unchanged page
/// stays as it is, and a changed one waits until nobody is working inside it.
/// </summary>
public sealed class RefreshGate
{
    private string? _shown;

    public bool ShouldRebuild(string signature, bool busy)
    {
        if (busy || signature == _shown)
        {
            return false;
        }

        _shown = signature;
        return true;
    }

    public void Invalidate() => _shown = null;
}
