using System.Windows;
using Waypoint.Core.Recording;

namespace Waypoint.App;

/// <summary>
/// Asks before recording, with where to record from and whether to keep the audio. Recording only ever starts
/// from Start recording here, or Record on the class start card.
/// </summary>
public partial class RecordWindow : Window
{
    /// <param name="classTitle">The class on now and its date, or null when no class is on.</param>
    public RecordWindow(Window owner, string? classTitle, AudioInput input, bool keepAudio)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        Question.Text = QuestionFor(classTitle);
        FromMicrophone.IsChecked = input == AudioInput.Microphone;
        FromComputer.IsChecked = input == AudioInput.ComputerAudio;
        FromBoth.IsChecked = input == AudioInput.Both;
        KeepAudio.IsChecked = keepAudio;
        Loaded += (_, _) => GoButton.Focus();
    }

    /// <summary>Names the class when one is on, else asks plainly rather than "Record Recording, Mon Nov 23?".</summary>
    public static string QuestionFor(string? classTitle) =>
        string.IsNullOrWhiteSpace(classTitle) ? "Record a lecture now?" : $"Record {classTitle}?";

    public AudioInput Input =>
        FromComputer.IsChecked == true ? AudioInput.ComputerAudio : FromBoth.IsChecked == true ? AudioInput.Both : AudioInput.Microphone;

    public bool KeepTheAudio => KeepAudio.IsChecked == true;

    private void OnGoClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
