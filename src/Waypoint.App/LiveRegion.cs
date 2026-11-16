using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace Waypoint.App;

/// <summary>
/// Messages that appear without focus moving to them, such as an undo bar or a form's problem, are read out by a
/// screen reader once they change, politely, after what it's already saying.
/// </summary>
internal static class LiveRegion
{
    public static void Announce(FrameworkElement element)
    {
        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);

        // Once the new text and visibility are laid out, so what's read is what's shown.
        element.Dispatcher.BeginInvoke(() =>
        {
            if (element.IsVisible && (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element)) is { } peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }, DispatcherPriority.Background);
    }
}
