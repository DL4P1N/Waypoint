using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>One pick in a form's row of choices.</summary>
public sealed record FormChoice(string Label, object Value)
{
    /// <summary>What a screen reader says for the choice, rather than the record's fields.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The Budget page's small forms. Each one is a few fields built in code, and Save hands them to a Core
/// method, the same one typing uses, with no sorter. A reply that asks something, or answers a question
/// like "Can I afford this?", shows in the form and keeps it open. Anything else closes it.
/// </summary>
public partial class BudgetFormWindow : Window
{
    /// <summary>What the budget says when a change needs the server and it's away. The page keeps what it last showed.</summary>
    public const string ServerAway = "Can't reach the server, so the budget can't change until it's back.";

    private readonly Dictionary<string, Field> _fields = [];
    private readonly HashSet<TextBox> _moneyBoxes = [];
    private readonly DateOnly _today;

    public BudgetFormWindow(Window owner, string heading, string button, DateOnly today)
    {
        InitializeComponent();
        WindowStyling.Apply(this);
        Owner = owner;
        Title = heading;
        Heading.Text = heading;
        DoneButton.Content = button;
        _today = today;
        Loaded += (_, _) => _fields.Values.Select(field => field.Input).OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    /// <summary>Runs on Save. Null or a reply that isn't an answer closes the form.</summary>
    public Func<BudgetFormWindow, OpReply?>? Submit { get; set; }

    /// <summary>What the last save said, for the page to show once the form closes.</summary>
    public string? Reply { get; private set; }

    /// <summary>The item the last save added, when it added one, so the page can offer to undo it.</summary>
    public long? AddedId { get; private set; }

    public void AddText(string key, string hint, string text = "", Func<bool>? visibleWhen = null)
    {
        var box = new TextBox { Text = text, Tag = hint, Margin = new Thickness(0, 10, 0, 0) };
        box.SetResourceReference(StyleProperty, "FieldBox");
        AutomationProperties.SetName(box, FieldName(hint));
        AutomationProperties.SetHelpText(box, hint);
        Add(key, box, box, visibleWhen);
    }

    /// <summary>A box is named for what goes in it, "Price", while the example stays as help, since the hint goes once typing starts.</summary>
    public static string FieldName(string hint)
    {
        var example = hint.IndexOf(", such as", StringComparison.Ordinal);
        return example > 0 ? hint[..example] : hint;
    }

    /// <summary>A money box. Blank reads as nothing, and anything that isn't an amount stops the save.</summary>
    public void AddMoney(string key, string hint, long? cents = null, Func<bool>? visibleWhen = null)
    {
        AddText(key, hint, cents is { } value ? (value / 100m).ToString("0.00", CultureInfo.InvariantCulture) : "", visibleWhen);
        _moneyBoxes.Add((TextBox)_fields[key].Input);
    }

    public void AddChoice(string key, string label, IReadOnlyList<FormChoice> choices, object? selected = null, Func<bool>? visibleWhen = null)
    {
        var list = new ListBox { ItemsSource = choices, DisplayMemberPath = nameof(FormChoice.Label) };
        list.SetResourceReference(StyleProperty, "ChoiceList");
        AutomationProperties.SetName(list, label);
        list.SelectedItem = choices.FirstOrDefault(choice => Equals(choice.Value, selected)) ?? choices.FirstOrDefault();
        list.SelectionChanged += (_, _) => ShowFields();

        var panel = new StackPanel();
        panel.Children.Add(Label(label));
        panel.Children.Add(list);
        Add(key, panel, list, visibleWhen);
    }

    /// <summary>
    /// A date, picked from a calendar. With <paramref name="quick"/>, Today and Yesterday sit beside it, since most
    /// purchases are added the day of or the day after.
    /// </summary>
    public void AddDate(string key, string label, DateOnly date, Func<bool>? visibleWhen = null, bool quick = false)
    {
        var button = new Button { Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Tag = date };
        button.SetResourceReference(BorderBrushProperty, "Rule");
        AutomationProperties.SetName(button, label);
        ShowDate(button);
        button.Click += (_, _) =>
        {
            var picker = new PickDateWindow(this, label, (DateOnly)button.Tag, null, asksForTime: false, action: "Choose");
            if (picker.ShowDialog() == true)
            {
                button.Tag = picker.Date;
                ShowDate(button);
            }
        };

        var panel = new StackPanel();
        panel.Children.Add(Label(label));
        if (!quick)
        {
            panel.Children.Add(button);
            Add(key, panel, button, visibleWhen);
            return;
        }

        var row = new WrapPanel();
        foreach (var (text, day) in new[] { ("Today", _today), ("Yesterday", _today.AddDays(-1)) })
        {
            var chip = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            chip.SetResourceReference(BorderBrushProperty, "Rule");
            chip.Click += (_, _) =>
            {
                button.Tag = day;
                ShowDate(button);
            };
            row.Children.Add(chip);
        }

        row.Children.Add(button);
        panel.Children.Add(row);
        Add(key, panel, button, visibleWhen);
    }

    /// <summary>A line of plain text, such as what's already set.</summary>
    public void AddNote(string text)
    {
        var note = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 0), FontSize = 13, TextWrapping = TextWrapping.Wrap };
        note.SetResourceReference(ForegroundProperty, "SecondaryText");
        Fields.Children.Add(note);
    }

    public void SetCancelText(string text) => CancelButton.Content = text;

    public string Text(string key) => _fields[key].Input is TextBox box ? box.Text.Trim() : "";

    public long? Cents(string key) => ParseCents(Text(key));

    public object? Choice(string key) => _fields[key].Input is ListBox { SelectedItem: FormChoice choice } ? choice.Value : null;

    public DateOnly Date(string key) => _fields[key].Input is Button { Tag: DateOnly date } ? date : _today;

    /// <summary>Reads "$1,250.50", "12", or "12.5" as cents. Anything else, or nothing, is null.</summary>
    public static long? ParseCents(string text)
    {
        var clean = text.Trim().Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        return decimal.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars) && dollars >= 0
            ? (long)Math.Round(dollars * 100m, MidpointRounding.AwayFromZero)
            : null;
    }

    private void Add(string key, FrameworkElement shown, FrameworkElement input, Func<bool>? visibleWhen)
    {
        _fields[key] = new Field(shown, input, visibleWhen);
        Fields.Children.Add(shown);
        ShowFields();
    }

    private void ShowFields()
    {
        foreach (var field in _fields.Values)
        {
            field.Shown.Visibility = field.VisibleWhen is null || field.VisibleWhen() ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6), FontSize = 13 };
        label.SetResourceReference(ForegroundProperty, "SecondaryText");
        return label;
    }

    private static void ShowDate(Button button) =>
        button.Content = ((DateOnly)button.Tag).ToString("ddd, MMM d", CultureInfo.InvariantCulture);

    private void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        var badMoney = _fields
            .Where(field => field.Value.Shown.Visibility == Visibility.Visible && field.Value.Input is TextBox box && IsMoney(box) && box.Text.Trim().Length > 0)
            .Any(field => Cents(field.Key) is null);
        if (badMoney)
        {
            Show("Try an amount like 12.50.");
            return;
        }

        OpReply? reply;
        try
        {
            reply = Submit?.Invoke(this);
        }
        catch (ServerUnavailableException)
        {
            // In server mode, with the server away. What was typed stays in the form, so it can be saved once it's back.
            Show($"{ServerAway} What you typed is still here.");
            return;
        }

        Reply = reply?.Text;
        AddedId = reply?.ItemId;
        if (reply is { IsAnswer: true })
        {
            Show(reply.Text);
            return;
        }

        DialogResult = true;
    }

    /// <summary>
    /// A longer answer leads with its first line, shown bold, and anything after a blank line is what it rests on,
    /// shown smaller and quieter, so the answer can be read at a glance.
    /// </summary>
    private void Show(string text)
    {
        Answer.Inlines.Clear();
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var main = split >= 0 ? text[..split] : text;
        var details = split >= 0 ? text[(split + 2)..] : null;
        var lineEnd = main.IndexOf('\n');
        if (lineEnd >= 0)
        {
            Answer.Inlines.Add(new Run(main[..lineEnd]) { FontWeight = FontWeights.SemiBold });
            Answer.Inlines.Add(new Run(main[lineEnd..]));
        }
        else
        {
            Answer.Inlines.Add(new Run(main));
        }

        if (!string.IsNullOrWhiteSpace(details))
        {
            Answer.Inlines.Add(new LineBreak());
            Answer.Inlines.Add(new LineBreak());
            var quiet = new Run(details) { FontSize = 12 };
            quiet.SetResourceReference(TextElement.ForegroundProperty, "SecondaryText");
            Answer.Inlines.Add(quiet);
        }

        Answer.Visibility = Visibility.Visible;
    }

    private bool IsMoney(TextBox box) => _moneyBoxes.Contains(box);

    private sealed record Field(FrameworkElement Shown, FrameworkElement Input, Func<bool>? VisibleWhen);
}
