using System;
using System.Threading.Tasks;
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
    private int _shown;
    private int _keepingFor = -1;

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

    /// <summary>
    /// In place of <see cref="Kept"/>, for a host whose card changes while a click is on its way: asked as the link
    /// is pressed, on the window's thread, for what keeps the click, so it's about the card as it was then.
    /// </summary>
    internal Func<Func<long>>? Keeping { get; set; }

    /// <summary>Adds the optional line to the kept click.</summary>
    public Action<long, string>? Noted { get; set; }

    /// <summary>
    /// How <see cref="Kept"/> and <see cref="Noted"/> reach where they're kept. On this PC's database they're saved
    /// before the click returns. In server mode they're sent off the window's thread, and the line opens or thanks
    /// once the server has them.
    /// </summary>
    internal PageWork Work { get; set; } = PageWork.Local;

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
        _shown++;
        _id = null;
        _note.Text = "";
        _box.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Collapsed;
        _link.Visibility = _showLink ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the optional line for a click already kept, with what it was about.</summary>
    public void Open(long id, string? about = null)
    {
        _shown++;
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
        if (_keepingFor == _shown || (Keeping?.Invoke() ?? Kept) is not { } keep)
        {
            return;
        }

        // Pressed once: the link goes while the click is on its way, so it's never kept twice. Once the card moves
        // on, its own link can be pressed.
        var shown = ++_shown;
        _keepingFor = shown;
        _link.Visibility = Visibility.Collapsed;
        Send(keep, id => Open(id), "Couldn't keep that while the server can't be reached.", shown);
    }

    /// <summary>
    /// Sends <paramref name="work"/> through <see cref="Work"/>, then <paramref name="done"/> or says
    /// <paramref name="problem"/>, unless the prompt has been reset or opened again since. Done at once, as on this
    /// PC's database, it all happens before this returns.
    /// </summary>
    private void Send<T>(Func<T> work, Action<T> done, string problem, int shown)
    {
        Task<T> sending;
        try
        {
            sending = Work.Run(work);
        }
        catch (Exception exception) when (Unreachable(exception))
        {
            Said(problem);
            return;
        }

        if (sending.IsCompleted)
        {
            done(sending.GetAwaiter().GetResult());
            return;
        }

        Later(sending, done, problem, shown);
    }

    private async void Later<T>(Task<T> sending, Action<T> done, string problem, int shown)
    {
        T result;
        try
        {
            result = await sending;
        }
        catch (Exception exception) when (Unreachable(exception))
        {
            if (shown == _shown)
            {
                Said(problem);
            }

            return;
        }

        if (shown == _shown)
        {
            done(result);
        }
    }

    private static bool Unreachable(Exception exception) =>
        exception is Core.Remote.ServerUnavailableException or Core.Remote.ServerTimedOutException or InvalidOperationException;

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
        var shown = ++_shown;
        if (_id is { } id && !string.IsNullOrWhiteSpace(note) && Noted is { } noted)
        {
            // The line goes while the note is on its way, so it's sent once and can't be typed into meanwhile.
            _id = null;
            _box.Visibility = Visibility.Collapsed;
            Send<object?>(() =>
            {
                noted(id, note);
                return null;
            }, _ => Said(Thanks), "The click was kept, but the note couldn't be saved while the server can't be reached.", shown);
            return;
        }

        Said(Thanks);
    }

    private const string Thanks = "Thanks. It's on Settings, under Feedback.";

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
