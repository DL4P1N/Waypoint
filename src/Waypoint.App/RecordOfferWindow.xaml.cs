using System;
using System.Windows;
using System.Windows.Input;

namespace Waypoint.App;

/// <summary>
/// The optional corner card at the start of a class. It only asks: Record starts recording, Not now closes it,
/// and it doesn't take focus from what's running. Turned on in Settings, and off unless the user turns it on.
/// </summary>
public partial class RecordOfferWindow : Window, ICornerCard
{
    private readonly Action _record;

    public RecordOfferWindow(string course, Action record)
    {
        _record = record;
        InitializeComponent();
        WindowStyling.Apply(this);
        Question.Text = $"Record {course}?";
        CornerCards.Track(this);
        System.Windows.Automation.AutomationProperties.SetHelpText(this, CornerCards.KeyHint);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
            }
        };
    }

    private void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        Close();
        _record();
    }

    private void OnNotNowClicked(object sender, RoutedEventArgs e) => Close();

    public void TakeFocus()
    {
        Activate();
        RecordButton.Focus();
    }

    /// <summary>Esc is Not now: the card closes and nothing records.</summary>
    public void Dismiss() => Close();
}
