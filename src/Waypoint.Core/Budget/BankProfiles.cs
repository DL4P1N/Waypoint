using System.Text.Json;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Budget;

/// <summary>
/// The banks the importers and alert readers know, one for each part they play: checking, two cards, a brokerage,
/// and a payment app. The built-in values describe example banks. A bank-profiles.json file next to settings.json
/// maps each one to your own bank's names, sender domains, and statement wording, and is read once at startup.
/// Names are also the account names purchases are kept under, so a profile's names must match the accounts already
/// in the database.
/// </summary>
public sealed class BankProfiles
{
    public const string FileName = "bank-profiles.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly AsyncLocal<BankProfiles?> Scoped = new();
    private static BankProfiles _loaded = new();

    private Regex? _transferWording;
    private Regex? _savingWords;

    public CheckingProfile Checking { get; init; } = new();

    /// <summary>The card whose alert emails are read line by line, and whose exports have no type column.</summary>
    public AlertCardProfile Card { get; init; } = new();

    /// <summary>The card whose exports have a type column (DEBIT or CREDIT).</summary>
    public RewardsCardProfile RewardsCard { get; init; } = new();

    public BrokerageProfile Brokerage { get; init; } = new();

    public PaymentAppProfile PaymentApp { get; init; } = new();

    /// <summary>
    /// Readable names for merchants, each a pair of the text a statement has (case ignored) and the name to show,
    /// such as ["BLUEPLATE*", "Blue Plate"]. They're tried before the built-in ones, in order, and the first match wins.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> MerchantNames { get; init; } = [];

    /// <summary>The profiles in use: those read at startup, or the built-in ones when there's no file.</summary>
    public static BankProfiles Current => Scoped.Value ?? Volatile.Read(ref _loaded);

    /// <summary>The cards by account name, alert card first.</summary>
    public IEnumerable<CardProfile> Cards => [Card, RewardsCard];

    /// <summary>
    /// Reads bank-profiles.json from the data folder and uses it from now on. Without one, the built-in profiles are
    /// used. A file that can't be read stops the start rather than filing purchases under the wrong names.
    /// </summary>
    public static BankProfiles Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var profiles = File.Exists(path) ? Parse(File.ReadAllText(path)) : new BankProfiles();
        Volatile.Write(ref _loaded, profiles);
        return profiles;
    }

    /// <summary>Profiles from JSON. Anything left out keeps its built-in value. Comments and trailing commas are allowed.</summary>
    public static BankProfiles Parse(string json)
    {
        BankProfiles? profiles;
        try
        {
            profiles = JsonSerializer.Deserialize<BankProfiles>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{FileName} couldn't be read: {exception.Message}", exception);
        }

        profiles ??= new BankProfiles();
        var names = new[] { profiles.Checking.Name, profiles.Card.Name, profiles.RewardsCard.Name, profiles.Brokerage.Name, profiles.PaymentApp.Name };
        if (names.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Every profile in {FileName} needs a name.");
        }

        var accounts = new[] { profiles.Checking.Name, profiles.Card.Name, profiles.RewardsCard.Name };
        if (accounts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != accounts.Length)
        {
            throw new InvalidDataException($"The checking and card names in {FileName} must each be different.");
        }

        if (string.IsNullOrWhiteSpace(profiles.RewardsCard.TypeColumn))
        {
            throw new InvalidDataException($"The rewards card's typeColumn in {FileName} can't be empty.");
        }

        if (profiles.MerchantNames is null || profiles.MerchantNames.Any(pair => pair is not { Count: 2 } || pair.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidDataException($"Each of the merchantNames in {FileName} needs to be a pair: the statement's text, then the name to show.");
        }

        profiles.Checking.Validate();
        profiles.PaymentApp.Validate();
        return profiles;
    }

    /// <summary>Uses these profiles in this flow of work only, until disposed. For tests.</summary>
    public static IDisposable Use(BankProfiles profiles)
    {
        var before = Scoped.Value;
        Scoped.Value = profiles;
        return new Restore(() => Scoped.Value = before);
    }

    /// <summary>The short name a purchase's source is shown by, from the account it was imported to.</summary>
    public string ShortNameOf(string account)
    {
        if (string.Equals(account, Checking.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Checking.ShortName ?? account;
        }

        return Cards.FirstOrDefault(card => string.Equals(account, card.Name, StringComparison.OrdinalIgnoreCase))?.ShortName ?? account;
    }

    /// <summary>The card a checking row pays, from its description, or null when it isn't one of the cards.</summary>
    public CardProfile? CardPaidBy(string description, bool atStart) =>
        Cards.FirstOrDefault(card => card.PaymentDescriptors.Any(text => atStart
            ? description.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            : description.Contains(text, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Wording in a checking row that says money moved to savings or investing.</summary>
    public Regex TransferWording() => _transferWording ??= new Regex(
        string.Join('|', new[] { "TRANSFER", "SAVINGS", "INVEST", "BROKERAGE", Brokerage.Name }.Concat(Brokerage.CheckingDescriptors).Select(Regex.Escape)),
        RegexOptions.IgnoreCase);

    /// <summary>Words that make a scheduled bill a savings transfer or investing purchase.</summary>
    public Regex SavingWords() => _savingWords ??= new Regex(
        $@"\b(sav(e|es|ing|ings)|transfer\w*|invest\w*|{Regex.Escape(Brokerage.Name)}|cushion|brokerage|roth|ira|etf|index fund)\b",
        RegexOptions.IgnoreCase);

    /// <summary>A wording setting as a regular expression, or why it can't be used.</summary>
    internal static Regex CompileSetting(string pattern, string setting, RegexOptions options)
    {
        try
        {
            return new Regex(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"The {setting} in {FileName} isn't a regular expression that can be used: {exception.Message}", exception);
        }
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>The checking account, its export, and its alert emails.</summary>
public sealed class CheckingProfile
{
    private Regex? _purchasePrefix;
    private Regex? _referenceSuffix;
    private Regex? _availableBalance;

    /// <summary>The account name checking rows and purchases are kept under.</summary>
    public string Name { get; init; } = "Northwind Bank checking";

    /// <summary>How a purchase from checking is labeled in lists.</summary>
    public string? ShortName { get; init; } = "Checking";

    /// <summary>Where its balance alerts and daily summaries come from. Subdomains count, lookalikes don't.</summary>
    public IReadOnlyList<string> SenderDomains { get; init; } = ["northwind.example.com"];

    /// <summary>Column names that are all in the first line of a checking export.</summary>
    public IReadOnlyList<string> CsvHeaders { get; init; } = ["Details", "Balance"];

    /// <summary>
    /// A regular expression for the wording a card purchase starts with in a checking export, ignoring case. A row it
    /// matches at the start is spending, and what it matched is taken off to leave the merchant.
    /// </summary>
    public string PurchasePrefix { get; init; } = @"^(?:CARD )?PURCHASE(?: RETURN)?(?:\s+\d{2}/\d{2})?\s*";

    /// <summary>
    /// A regular expression for the reference number and anything after it at the end of a checking purchase,
    /// taken off to leave the merchant. Case matters here.
    /// </summary>
    public string ReferenceSuffix { get; init; } = @"\s+REF\s?\d{6,}.*$";

    /// <summary>
    /// A regular expression for the words just before the balance in a checking alert, ignoring case. The balance
    /// follows them after a colon or "is", as in "available balance: $40.00" or "available balance is -$150.00".
    /// </summary>
    public string BalanceWording { get; init; } = "available balance";

    public Regex PurchasePrefixPattern() => _purchasePrefix ??= Compile(PurchasePrefix, nameof(PurchasePrefix), RegexOptions.IgnoreCase);

    public Regex ReferenceSuffixPattern() => _referenceSuffix ??= Compile(ReferenceSuffix, nameof(ReferenceSuffix), RegexOptions.None);

    /// <summary>The balance wording followed by a dollar amount, with groups minus and amount.</summary>
    public Regex AvailableBalancePattern() => _availableBalance ??= Compile(
        $@"(?:{BalanceWording})(?::|\s+is)\s*(?<minus>-)?\$\s?(?<amount>[\d,]+\.\d{{2}})", nameof(BalanceWording), RegexOptions.IgnoreCase);

    /// <summary>Checks that every wording setting is a regular expression that can be used.</summary>
    internal void Validate()
    {
        PurchasePrefixPattern();
        ReferenceSuffixPattern();
        AvailableBalancePattern();
    }

    private static Regex Compile(string pattern, string setting, RegexOptions options) =>
        BankProfiles.CompileSetting(pattern, $"checking {setting}", options);
}

/// <summary>A credit card: its account name, alert senders, and how checking describes paying it.</summary>
public abstract class CardProfile
{
    public string Name { get; init; } = "";

    /// <summary>How a purchase on the card is labeled in lists. Null shows the name.</summary>
    public string? ShortName { get; init; }

    public IReadOnlyList<string> SenderDomains { get; init; } = [];

    /// <summary>How a payment to this card starts in a checking export, and appears in a checking summary email.</summary>
    public IReadOnlyList<string> PaymentDescriptors { get; init; } = [];

    /// <summary>How the card's rewards credits read in its export. Like payments, they lower the balance but aren't refunds.</summary>
    public IReadOnlyList<string> CreditDescriptors { get; init; } = [];

    /// <summary>
    /// How paying the card reads in its own export, besides PAYMENT, AUTOPAY, and STATEMENT CREDIT, which always count.
    /// A row with any of these lowers the balance and isn't spending.
    /// </summary>
    public IReadOnlyList<string> ExportPaymentDescriptors { get; init; } = [];

    /// <summary>The name of the purchase date column in the card's export, besides "Transaction Date" and "Date".</summary>
    public IReadOnlyList<string> CsvDateHeaders { get; init; } = [];
}

/// <summary>The card whose alert emails say Merchant:, Date:, and Amount: under one subject.</summary>
public sealed class AlertCardProfile : CardProfile
{
    public AlertCardProfile()
    {
        Name = "Contoso Card";
        SenderDomains = ["contoso.example.com"];
        PaymentDescriptors = ["CONTOSO CARD"];
        CreditDescriptors = ["REWARD CREDIT"];
        CsvDateHeaders = ["Purchase Date"];
    }

    /// <summary>
    /// The only subject a purchase alert has. The card's other emails about the same purchase are skipped. An alert
    /// that can't be read is kept to fix the reader from only when this has "purchase", "transaction", or "charge" in it.
    /// </summary>
    public string AlertSubject { get; init; } = "Purchase alert";
}

/// <summary>The card whose exports have a type column. Its alerts are read by their wording.</summary>
public sealed class RewardsCardProfile : CardProfile
{
    public RewardsCardProfile()
    {
        Name = "Lucerne Card";
        ShortName = "Lucerne Card";
        SenderDomains = ["lucerne.example.com"];
        PaymentDescriptors = ["LUCERNE CARD"];
    }

    /// <summary>
    /// The name of the column in the card's export, ignoring case, that says DEBIT or CREDIT. A file with it is this
    /// card's, and shows purchases as negative amounts. Its date, description, and amount columns are found by their
    /// usual names.
    /// </summary>
    public string TypeColumn { get; init; } = "Type";
}

/// <summary>Where savings transfers and investing go, and its deposit confirmation emails.</summary>
public sealed class BrokerageProfile
{
    public string Name { get; init; } = "Fabrikam";

    public IReadOnlyList<string> SenderDomains { get; init; } = ["fabrikam.example.com"];

    /// <summary>How money moved to it appears in a checking export.</summary>
    public IReadOnlyList<string> CheckingDescriptors { get; init; } = ["FABRIKAM INV"];
}

/// <summary>The payment app people pay you back through, and how its emails and checking rows read.</summary>
public sealed class PaymentAppProfile
{
    private Regex? _forwardedHeader;
    private Regex? _notNote;
    private Regex? _received;
    private Regex? _outgoing;
    private Regex? _noteLabel;

    public string Name { get; init; } = "Tailspin Pay";

    public IReadOnlyList<string> SenderDomains { get; init; } = ["tailspinpay.example.com"];

    /// <summary>How the app's rows start in a checking export.</summary>
    public IReadOnlyList<string> CheckingDescriptors { get; init; } = ["TAILSPIN PAY"];

    /// <summary>
    /// A regular expression (case ignored) for the subject of an email saying someone paid you, with the groups payer
    /// and amount. A forward's Fw: or Fwd: is taken off first.
    /// </summary>
    public string ReceivedPattern { get; init; } = @"^(?!you\b)(?<payer>.{1,60}?) sent you \$\s?(?<amount>[\d,]+\.\d{2})\.?$";

    /// <summary>
    /// A regular expression (case ignored) for the subject of money you sent, or a request for it. Neither is a
    /// repayment, so these emails are skipped rather than kept to fix the reader from.
    /// </summary>
    public string OutgoingPattern { get; init; } = @"^(?:you (?:sent|requested|asked)\b|.* asked you for\b)";

    /// <summary>Wording (case ignored) on the body line just before a note that has no label.</summary>
    public string NoteAfter { get; init; } = "sent you";

    /// <summary>Labels a note can start with in the body, before a colon, as in "Note: tacos".</summary>
    public IReadOnlyList<string> NoteLabels { get; init; } = ["Note", "Message"];

    /// <summary>
    /// Wording (case ignored) that makes a line part of the email around a payment rather than its note, besides
    /// amounts, links, addresses, long numbers, and the app's name.
    /// </summary>
    public IReadOnlyList<string> NotNoteWords { get; init; } = ["view payment"];

    /// <summary>Whether a checking row is the app's, anywhere in its description.</summary>
    public bool Mentions(string description) =>
        CheckingDescriptors.Any(text => description.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a checking row starts as the app's.</summary>
    public bool Starts(string description) =>
        CheckingDescriptors.Any(text => description.StartsWith(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>A quoted From or Sender line at one of the app's domains, as a forward shows it.</summary>
    public Regex ForwardedHeader() => _forwardedHeader ??= new Regex(
        $@"^\s*(?:From|Sender):[^\r\n]*@(?:[a-z0-9-]+\.)*(?:{string.Join('|', SenderDomains.Select(Regex.Escape))})>?[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>A line that's part of the email around a payment rather than its note.</summary>
    public Regex NotNote() => _notNote ??= new Regex(
        string.Join('|', new[] { @"\$", "https?:" }.Concat(Words(NotNoteWords)).Append(Regex.Escape(Name))
            .Concat(CheckingDescriptors.Select(Regex.Escape)).Append("@").Append(@"\d{4}")),
        RegexOptions.IgnoreCase);

    /// <summary>The subject of a payment someone sent you, with groups payer and amount.</summary>
    public Regex ReceivedSubject() => _received ??= BankProfiles.CompileSetting(ReceivedPattern, "payment app receivedPattern", RegexOptions.IgnoreCase);

    /// <summary>The subject of money you sent, or a request for it.</summary>
    public Regex OutgoingSubject() => _outgoing ??= BankProfiles.CompileSetting(OutgoingPattern, "payment app outgoingPattern", RegexOptions.IgnoreCase);

    /// <summary>A labeled note line, with the note in group 1.</summary>
    public Regex NoteLabel() => _noteLabel ??= new Regex(
        $@"^\s*(?:{string.Join('|', Words(NoteLabels).DefaultIfEmpty("(?!)"))}):\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Checks that the wording settings are regular expressions that can be used, with the groups they need.</summary>
    internal void Validate()
    {
        var groups = ReceivedSubject().GetGroupNames();
        if (!groups.Contains("payer") || !groups.Contains("amount"))
        {
            throw new InvalidDataException($"The payment app receivedPattern in {BankProfiles.FileName} needs groups named payer and amount.");
        }

        OutgoingSubject();
    }

    private static IEnumerable<string> Words(IEnumerable<string> words) =>
        words.Select(word => word.Trim()).Where(word => word.Length > 0).Select(Regex.Escape);
}
