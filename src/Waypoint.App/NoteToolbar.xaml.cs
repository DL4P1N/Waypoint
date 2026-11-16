using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Waypoint.App;

/// <summary>
/// The strip of formatting buttons above the note. It follows the note editor it's attached to: each button
/// shows whether the caret or selection already has that formatting, and does nothing the keys don't.
/// </summary>
public partial class NoteToolbar : UserControl
{
    private MarkdownBox? _box;
    private bool _syncing;

    public NoteToolbar()
    {
        InitializeComponent();
        PreviewKeyDown += OnToolbarKey;
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyClick));
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (!IsKeyboardFocusWithin)
            {
                SetFocusable(false);
            }
        };
    }

    /// <summary>The text style the toolbar says is on, such as Normal or Heading 2.</summary>
    public string StyleName => StyleText.Text;

    /// <summary>Makes the buttons act on <paramref name="box"/> and follow its caret.</summary>
    public void Attach(MarkdownBox box)
    {
        if (_box is not null)
        {
            _box.FormatChanged -= OnFormatChanged;
            _box.PreviewKeyDown -= OnNoteKey;
        }

        _box = box;
        box.FormatChanged += OnFormatChanged;
        box.PreviewKeyDown += OnNoteKey;
        foreach (var button in Buttons())
        {
            button.CommandTarget = box;
        }

        Sync();
    }

    private IEnumerable<ButtonBase> Buttons() => Strip.Children.OfType<ButtonBase>().Where(button => button.Command is not null);

    /// <summary>Every button, in order, including the text style menu.</summary>
    private IEnumerable<ButtonBase> AllButtons() => Strip.Children.OfType<ButtonBase>();

    private void OnNoteKey(object sender, KeyEventArgs e)
    {
        if (HandleNoteKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>A key in the note. Alt+F10 moves into the toolbar, as in many editors, on its first button.</summary>
    internal bool HandleNoteKey(Key key, ModifierKeys modifiers) =>
        key == Key.F10 && modifiers == ModifierKeys.Alt && IsVisible && FocusButton(0);

    /// <summary>
    /// The buttons only take focus while the keyboard is in the toolbar, so a click never takes it from the note.
    /// </summary>
    private void SetFocusable(bool focusable)
    {
        foreach (var button in AllButtons())
        {
            button.Focusable = focusable;
        }
    }

    private bool FocusButton(int index)
    {
        var usable = AllButtons().Where(button => button.IsEnabled && button.IsVisible).ToList();
        if (usable.Count == 0)
        {
            return false;
        }

        SetFocusable(true);
        return usable[((index % usable.Count) + usable.Count) % usable.Count].Focus();
    }

    /// <summary>Left and Right go between the buttons, wrapping round, Home and End go to the ends, and Esc or Tab goes back to the note.</summary>
    private void OnToolbarKey(object sender, KeyEventArgs e)
    {
        if (HandleToolbarKey(e.Key))
        {
            e.Handled = true;
        }
    }

    internal bool HandleToolbarKey(Key key)
    {
        var usable = AllButtons().Where(button => button.IsEnabled && button.IsVisible).ToList();
        var at = usable.FindIndex(button => button.IsKeyboardFocused);
        if (at < 0)
        {
            return false;
        }

        switch (key)
        {
            case Key.Right:
                return FocusButton(at + 1);
            case Key.Left:
                return FocusButton(at - 1);
            case Key.Home:
                return FocusButton(0);
            case Key.End:
                return FocusButton(usable.Count - 1);
            case Key.Escape:
            case Key.Tab:
                BackToNote();
                return true;
            default:
                return false;
        }
    }

    private void BackToNote()
    {
        SetFocusable(false);
        _box?.Focus();
    }

    /// <summary>
    /// Using a button from the keyboard goes back to the note, to carry on typing. It goes before the button's command
    /// runs, so a form the command opens, such as Link, comes back to the note too. The style menu goes back once it's closed.
    /// </summary>
    private void OnAnyClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != StyleButton && e.OriginalSource is ButtonBase { IsKeyboardFocused: true })
        {
            BackToNote();
        }
    }

    private void OnFormatChanged(object? sender, EventArgs e) => Sync();

    /// <summary>Shows what's on at the caret. Every button's pressed state comes from the note, never from its own clicks.</summary>
    internal void Sync()
    {
        if (_box is not { } box || _syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            var format = box.CurrentFormat();
            BoldButton.IsChecked = format.Bold;
            ItalicButton.IsChecked = format.Italic;
            UnderlineButton.IsChecked = format.Underline;
            StrikeButton.IsChecked = format.Strike;
            CodeButton.IsChecked = format.Code;
            LinkButton.IsChecked = format.Link;
            BulletsButton.IsChecked = format.List == "Bullet";
            NumbersButton.IsChecked = format.List == "Number";
            ChecklistButton.IsChecked = format.List == "Task";
            QuoteButton.IsChecked = format.Quote;
            StyleText.Text = format.Heading > 0 ? $"Heading {format.Heading}" : "Normal";
            StyleButton.IsChecked = format.Heading > 0;
            StyleButton.IsEnabled = format.Editable;
            CommandManager.InvalidateRequerySuggested();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>A click flips a button on its own. The note decides what's really on, so it's put back to that.</summary>
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        Dispatcher.BeginInvoke(Sync, DispatcherPriority.Input);
    }

    private void OnStyleClicked(object sender, RoutedEventArgs e)
    {
        if (_box is not { } box)
        {
            return;
        }

        StyleButton.IsChecked = box.CurrentFormat().Heading > 0;
        var entries = new List<RowMenuEntry>
        {
            new("Normal", () => Choose(0)),
            new("Heading 1", () => Choose(1)),
            new("Heading 2", () => Choose(2)),
            new("Heading 3", () => Choose(3)),
        };

        RowMenu.Open(StyleButton, PlacementMode.Bottom, entries, () => box.Focus());
    }

    /// <summary>The same thing Ctrl+Alt+0 to 3 do.</summary>
    internal void Choose(int heading)
    {
        if (_box is not { } box)
        {
            return;
        }

        NoteCommands.Heading.Execute(heading, box);
        box.Focus();
        Sync();
    }
}
