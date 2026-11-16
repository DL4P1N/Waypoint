using System;
using System.Windows;
using System.Windows.Input;

namespace Waypoint.App;

public partial class CaptureWindow : Window
{
    private readonly Action<string> _submitted;
    private readonly Action<string[]> _filesDropped;

    public CaptureWindow(Action<string> submitted, Action<string[]> filesDropped)
    {
        _submitted = submitted;
        _filesDropped = filesDropped;
        InitializeComponent();
        WindowStyling.Apply(this);
    }

    public void ShowForCapture()
    {
        Show();
        Activate();
        Input.Focus();
        Input.SelectAll();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            var text = Input.Text;
            Input.Clear();
            Hide();
            _submitted(text);
        }
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            Hide();
            _filesDropped(paths);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();
}
