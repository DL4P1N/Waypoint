using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Waypoint.Core.Capture;

namespace Waypoint.App;

/// <summary>
/// "This didn't help" on a card: a small link that keeps the click as soon as it's pressed, then offers one optional
/// line in place, with Add and No note. Nothing is asked after that. A host without the link, such as To do's row menu,
/// opens the line straight away with <see cref="Open"/>.
/// </summary>
public sealed class FeedbackPrompt : StackPanel
{
    private readonly Button _link;
    private readonly StackPanel _box;
    private readonly TextBlock _ask;
    private readonly TextBox _note;
    private readonly TextBlock _done;
    private long? _id;
    private bool _showLink = true;

    /// <summary>The button that keeps the click without a line. Not Skip, which on a card means skipping the reminder.</summary>
    public const string NoNote = "No note";

    public FeedbackPrompt()
    {
        _link = new Button { Content = CardLabels.DidntHelp, FontSize = 12, Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 6, 0, 0), ToolTip = "Keeps a note that this didn't help, for you to read on Settings under Feedback" };
        _link.SetResourceReference(StyleProperty, "LinkButton");
        _link.Click += (_, _) => Give();
        AutomationNames(_link, CardLabels.DidntHelp);

        _ask = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4), Text = "Noted. Anything to add? You can leave this blank." };
        _ask.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        _note = new TextBox { MaxLength = Feedback.MaxNote, FontSize = 12, Padding = new Thickness(4, 3, 4, 3) };
        AutomationNames(_note, "What would have helped");
        _note.KeyDown += OnNoteKey;
        var add = new Button { Content = "Add", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 6, 0), FontSize = 12 };
        add.Click += (_, _) => Finish(_note.Text);
        var skip = new Button { Content = NoNote, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        skip.SetResourceReference(StyleProperty, "LinkButton");
        skip.Click += (_, _) => Finish(null);
        AutomationNames(skip, "Keep it without a note");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(add);
        buttons.Children.Add(skip);
        _box = new StackPanel { Visibility = Visibility.Collapsed };
        _box.Children.Add(_ask);
        _box.Children.Add(_note);
        _box.Children.Add(buttons);

        _done = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _done.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");

        Children.Add(_link);
        Children.Add(_box);
        Children.Add(_done);
    }

    /// <summary>Keeps the click and returns its ID. Set by the host, which knows what the card was about.</summary>
    public Func<long>? Kept { get; set; }

    /// <summary>Adds the optional line to the kept click.</summary>
    public Action<long, string>? Noted { get; set; }

    /// <summary>False for a host that opens the line itself, such as the row menu, so no link shows.</summary>
    public bool ShowLink
    {
        get => _showLink;
        set
        {
            _showLink = value;
            Reset();
        }
    }

    /// <summary>Back to the plain link, for when the card moves on to something else.</summary>
    public void Reset()
    {
        _id = null;
        _note.Text = "";
        _box.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Collapsed;
        _link.Visibility = _showLink ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the optional line for a click already kept, with what it was about.</summary>
    public void Open(long id, string? about = null)
    {
        _id = id;
        _ask.Text = about is { Length: > 0 } ? $"Noted for {about}. Anything to add? You can leave this blank." : "Noted. Anything to add? You can leave this blank.";
        _note.Text = "";
        _link.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Collapsed;
        _box.Visibility = Visibility.Visible;
        _note.Focus();
    }

    private void Give()
    {
        if (Kept is not { } keep)
        {
            return;
        }

        try
        {
            Open(keep());
        }
        catch (Exception exception) when (exception is Core.Remote.ServerUnavailableException or Core.Remote.ServerTimedOutException or InvalidOperationException)
        {
            Said("Couldn't keep that while the server can't be reached.");
        }
    }

    private void OnNoteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Finish(_note.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
    }

    private void Finish(string? note)
    {
        if (_id is { } id && !string.IsNullOrWhiteSpace(note))
        {
            try
            {
                Noted?.Invoke(id, note);
            }
            catch (Exception exception) when (exception is Core.Remote.ServerUnavailableException or Core.Remote.ServerTimedOutException or InvalidOperationException)
            {
                Said("The click was kept, but the note couldn't be saved while the server can't be reached.");
                return;
            }
        }

        Said("Thanks. It's on Settings, under Feedback.");
    }

    private void Said(string text)
    {
        _id = null;
        _box.Visibility = Visibility.Collapsed;
        _link.Visibility = Visibility.Collapsed;
        _done.Text = text;
        _done.Visibility = Visibility.Visible;
    }

    private static void AutomationNames(FrameworkElement element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}
