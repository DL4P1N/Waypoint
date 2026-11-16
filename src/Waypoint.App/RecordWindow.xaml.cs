using System.Windows;
using Waypoint.Core.Recording;

namespace Waypoint.App;

/// <summary>
/// Asks before recording, with where to record from and whether to keep the audio. Recording only ever starts
/// from Start recording here, or Record on the class start card.
/// </summary>
public partial class RecordWindow : Window
{
    public RecordWindow(Window owner, string title, AudioInput input, bool keepAudio)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        Question.Text = $"Record {title}?";
        FromMicrophone.IsChecked = input == AudioInput.Microphone;
        FromComputer.IsChecked = input == AudioInput.ComputerAudio;
        FromBoth.IsChecked = input == AudioInput.Both;
        KeepAudio.IsChecked = keepAudio;
        Loaded += (_, _) => GoButton.Focus();
    }

    public AudioInput Input =>
        FromComputer.IsChecked == true ? AudioInput.ComputerAudio : FromBoth.IsChecked == true ? AudioInput.Both : AudioInput.Microphone;

    public bool KeepTheAudio => KeepAudio.IsChecked == true;

    private void OnGoClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
