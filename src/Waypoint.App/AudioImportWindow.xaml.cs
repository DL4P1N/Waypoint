using System.Collections.Generic;
using System.Windows;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>
/// Asks before a recording is transcribed, with the "Keep the audio" choice. The first time, it also asks to
/// download the speech to text files and says how big they are. Nothing downloads without a yes here, and
/// Cancel is the default then, so a stray Enter never starts a 1.6 GB download.
/// </summary>
public partial class AudioImportWindow : Window
{
    public AudioImportWindow(Window owner, IReadOnlyList<string> names, long? downloadBytes)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        Question.Text = names.Count == 1 ? $"Make notes from {names[0]}?" : $"Make notes from these {names.Count} recordings?";
        Detail.Text = "Waypoint turns the recording into text on this PC. An hour of audio takes about 10 minutes, and you can keep working meanwhile. "
            + "The audio never leaves this PC. Only the text is sent to Claude to write the notes.";

        if (downloadBytes is { } bytes)
        {
            DownloadText.Text = $"First, Waypoint needs to download the speech to text model and the program that runs it, about {TranscriptionQueue.Size(bytes)} in all. "
                + "This happens once. They come from Hugging Face and GitHub and are checked before they're used.";
            GoButton.Content = "Download and start";
            CancelButton.IsDefault = true;
            Loaded += (_, _) => CancelButton.Focus();
        }
        else
        {
            DownloadText.Visibility = Visibility.Collapsed;
            GoButton.Content = "Start";
            GoButton.IsDefault = true;
            Loaded += (_, _) => GoButton.Focus();
        }
    }

    public bool KeepTheAudio => KeepAudio.IsChecked == true;

    private void OnGoClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
