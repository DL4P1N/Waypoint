using System;
using System.Windows;

namespace Waypoint.App;

/// <summary>
/// The optional corner card at the start of a class. It only asks: Record starts recording, Not now closes it,
/// and it doesn't take focus from what's running. Turned on in Settings, and off unless the user turns it on.
/// </summary>
public partial class RecordOfferWindow : Window
{
    private const int ScreenMargin = 16;

    private readonly Action _record;

    public RecordOfferWindow(string course, Action record)
    {
        _record = record;
        InitializeComponent();
        WindowStyling.Apply(this);
        Question.Text = $"Record {course}?";
        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Right - ActualWidth - ScreenMargin;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - ScreenMargin;
        };
    }

    private void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        Close();
        _record();
    }

    private void OnNotNowClicked(object sender, RoutedEventArgs e) => Close();
}
