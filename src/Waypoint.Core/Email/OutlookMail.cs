using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;

namespace Waypoint.Core.Email;

/// <summary>
/// The app registration's ID. This isn't a secret, and sign-in happens on Microsoft's own page.
/// OwnAddress is your own email address, the one payment app emails are forwarded from. A forward from anyone else isn't counted.
/// </summary>
public sealed record OutlookSettings(string ClientId, string? OwnAddress = null);

public sealed record DeviceCode(string UserCode, string VerificationUri, string Code, int IntervalSeconds, int ExpiresSeconds);

/// <summary>Keeps the sign-in between runs. The app encrypts it, so Core never touches the disk for it.</summary>
public interface ITokenStore
{
    string? Load();

    void Save(string token);
}

/// <summary>A purchase read from an alert email, and which card it was on.</summary>
public sealed record EmailPurchase(string Account, ImportedTransaction Transaction);

/// <summary>An alert the parser couldn't read, kept so the parser can be fixed.</summary>
public sealed record UnreadAlert(string From, string Subject, string Body);

/// <summary>Money someone sent through the payment app, as read from its email.</summary>
public sealed record PaymentAppPayment(string Payer, long Cents, string? Note);

/// <summary>A payment app payment waiting to be matched to a purchase.</summary>
public sealed record PaymentAppRepayment(string MessageId, string Payer, long Cents, string? Note, DateOnly ReceivedOn);

/// <summary>What came of a new payment. Matched is false if the text is a question. MessageId is the payment's email.</summary>
public sealed record PaymentAppOutcome(string Text, bool Matched, string? MessageId = null);

/// <summary>
/// Reads purchase alerts from the alerts inbox with read-only access. No password is seen or stored,
/// and only the amount, merchant, and date of a purchase are kept.
/// </summary>
public sealed class OutlookMail(HttpClient http, WaypointDatabase database, OutlookSettings settings, ITokenStore tokens)
{
    private const string LoginBase = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string MessagesUrl = "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages";
    private const string Scope = "Mail.Read offline_access";
    private const string LastMailKey = "outlook_last_received";
    private const string SeenMailKey = "outlook_seen_at_last";
    private const string SignedOutKey = "outlook_signed_out";
    private const int FirstLookBackDays = 7;
    private const int MaxFetched = 25;

    private readonly SemaphoreSlim _polling = new(1, 1);

    public string? LastError { get; private set; }

    /// <summary>The latest bank or payment app email left out because it failed the sender check, said plainly.</summary>
    public string? SkippedSender { get; private set; }

    /// <summary>Signed in, with a sign-in Microsoft still takes.</summary>
    public bool IsConnected => tokens.Load() is not null && !NeedsSignIn;

    /// <summary>
    /// Microsoft turned the kept sign-in down, as when it expires or the password changes, so it has to be done
    /// again. Kept in the database, so it still shows after a restart, and cleared by signing in.
    /// </summary>
    public bool NeedsSignIn => tokens.Load() is not null && database.GetPreference(SignedOutKey) == "1";

    public async Task<DeviceCode> BeginSignInAsync(CancellationToken cancellationToken = default)
    {
        using var document = await PostFormAsync($"{LoginBase}/devicecode", new() { ["client_id"] = settings.ClientId, ["scope"] = Scope }, cancellationToken);
        var root = document.RootElement;
        return new DeviceCode(
            root.GetProperty("user_code").GetString()!,
            root.GetProperty("verification_uri").GetString()!,
            root.GetProperty("device_code").GetString()!,
            root.GetProperty("interval").GetInt32(),
            root.GetProperty("expires_in").GetInt32());
    }

    /// <summary>Waits for the user to approve on Microsoft's page. Returns false if it runs out of time.</summary>
    public async Task<bool> FinishSignInAsync(DeviceCode code, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(code.IntervalSeconds), cancellationToken);
            using var document = await PostFormAsync($"{LoginBase}/token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = settings.ClientId,
                ["device_code"] = code.Code,
            }, cancellationToken, allowErrors: true);

            if (document.RootElement.TryGetProperty("refresh_token", out var refresh))
            {
                tokens.Save(refresh.GetString()!);
                database.SetPreference(SignedOutKey, "");
                return true;
            }

            var error = document.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
            if (error is not "authorization_pending" and not "slow_down")
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Hands over new purchases, stores them, and reports alerts it couldn't read.</summary>
    public async Task PollAsync(Action<EmailPurchase> onPurchase, Action<UnreadAlert> onUnread, Action<long>? onDeposit = null, Action<PaymentAppOutcome>? onPaymentApp = null, CancellationToken cancellationToken = default)
    {
        // A sign-in Microsoft turned down isn't tried again until it's done afresh.
        if (tokens.Load() is not { } refreshToken || NeedsSignIn || !await _polling.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            using var tokenDocument = await PostFormAsync($"{LoginBase}/token", new()
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = settings.ClientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope,
            }, cancellationToken, allowErrors: true);
            var tokenRoot = tokenDocument.RootElement;
            if (Text(tokenRoot, "error") is { } error)
            {
                // An expired or withdrawn sign-in shows as needing sign-in, not as connected. Anything else is a
                // problem with this try, and the next one goes ahead as usual.
                if (error is "invalid_grant" or "interaction_required")
                {
                    database.SetPreference(SignedOutKey, "1");
                    LastError = SignInEnded;
                    return;
                }

                throw new HttpRequestException(Text(tokenRoot, "error_description") ?? $"Microsoft sign-in said {error}.");
            }

            tokens.Save(Text(tokenRoot, "refresh_token") ?? throw new HttpRequestException("Microsoft sign-in sent no token."));
            var accessToken = Text(tokenRoot, "access_token") ?? throw new HttpRequestException("Microsoft sign-in sent no token.");

            var since = database.GetPreference(LastMailKey)
                ?? DateTimeOffset.UtcNow.AddDays(-FirstLookBackDays).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var filter = Uri.EscapeDataString($"receivedDateTime gt {since}");
            var url = $"{MessagesUrl}?$select=id,subject,from,receivedDateTime,body,internetMessageHeaders&$orderby=receivedDateTime&$top={MaxFetched}&$filter={filter}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            // Graph gives whole seconds but filters on the full time, so the last message comes back
            // every poll until a newer one arrives. The ones already read at that second are skipped.
            var seen = (database.GetPreference(SeenMailKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            string? problem = null;
            foreach (var message in document.RootElement.GetProperty("value").EnumerateArray())
            {
                // One without an ID or time can't be marked as read, so it's passed over rather than stopping the rest.
                if (Text(message, "id") is not { } id || Text(message, "receivedDateTime") is not { } received
                    || !DateTimeOffset.TryParse(received, CultureInfo.InvariantCulture, DateTimeStyles.None, out var receivedAt))
                {
                    problem = UnreadableEmail;
                    continue;
                }

                if (seen.Contains(id))
                {
                    continue;
                }

                // One email that can't be read doesn't stop the ones after it, and the read mark still moves past it,
                // so it isn't read again every few minutes. Trouble saving is different: the round stops there, and
                // the email is read again next time.
                try
                {
                    Read(message, id, receivedAt.ToLocalTime(), onPurchase, onUnread, onDeposit, onPaymentApp);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or Microsoft.Data.Sqlite.SqliteException))
                {
                    problem = UnreadableEmail;
                }

                if (received != since)
                {
                    seen.Clear();
                    since = received;
                }

                seen.Add(id);

                // Both together, so the time read to and the messages already read at that time always agree.
                database.SetPreferences((LastMailKey, received), (SeenMailKey, string.Join('\n', seen)));
            }

            LastError = problem;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LastError = exception.Message;
        }
        finally
        {
            _polling.Release();
        }
    }

    /// <summary>Said when Microsoft no longer takes the kept sign-in.</summary>
    public const string SignInEnded = "Microsoft ended the sign-in, so alert email needs to be connected again.";

    /// <summary>Said when an email came back in a shape that couldn't be read. It's passed over, and the rest are read.</summary>
    public const string UnreadableEmail = "An email couldn't be read, so it was skipped.";

    /// <summary>A string found by following property names, or null when one is missing or isn't text.</summary>
    private static string? Text(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private void Read(JsonElement message, string id, DateTimeOffset received, Action<EmailPurchase> onPurchase, Action<UnreadAlert> onUnread, Action<long>? onDeposit, Action<PaymentAppOutcome>? onPaymentApp)
    {
        // Mail with no sender or subject, such as a draft or a bounce, is read as blank rather than stopping here.
        var from = Text(message, "from", "emailAddress", "address") ?? "";
        var subject = Text(message, "subject") ?? "";
        var body = Text(message, "body", "content") ?? "";

        // A reply from someone a task is waiting on brings that task back with one nudge. Only who sent it and the subject are used.
        var sender = Text(message, "from", "emailAddress", "name");
        database.NoticeReply(from, sender, subject, received.DateTime);

        // Money is only read from mail that really came from its sender. Anyone can type a bank's name into From.
        if (!AlertParser.PassedDmarc(AuthenticationResults(message), from))
        {
            // A bank's own domain failing the check is worth saying, since it may mean forwarding broke the check
            // and real alerts are being left out. Anyone else is just ignored.
            if (AlertParser.LooksLikeMoney(from, subject) || AlertParser.IsCheckingAlert(from) || AlertParser.IsPaymentApp(from, "", null))
            {
                SkippedSender = $"An email from {AlertParser.DomainOf(from)} on {received:MMM d} didn't pass the sender check, so it wasn't used.";
            }

            return;
        }

        // The checking bank says checking's balance outright, and its daily summary lists card payments.
        if (AlertParser.IsCheckingAlert(from))
        {
            var on = DateOnly.FromDateTime(received.DateTime);
            if (AlertParser.ReadAvailableBalance(body) is { } balance)
            {
                database.SetCheckingFromAlert(balance, received);
            }

            foreach (var (account, payment) in AlertParser.ReadCardPayments(body, on))
            {
                database.AddCardPayments(account, [payment]);
            }

            return;
        }

        if (AlertParser.IsPaymentApp(from, body, settings.OwnAddress))
        {
            if (AlertParser.ReadPaymentApp(from, subject, body, settings.OwnAddress) is { } payment)
            {
                if (database.AddRepayment(id, payment, DateOnly.FromDateTime(received.DateTime)) is { } outcome)
                {
                    onPaymentApp?.Invoke(outcome);
                }
            }
            else if (!AlertParser.IsPaymentAppOutgoing(subject))
            {
                onUnread(new UnreadAlert(from, subject, body));
            }

            return;
        }

        if (AlertParser.ReadDeposit(from, subject, body) is { } deposit)
        {
            // Already counted means already told, so the same email read again after a crash sends nothing.
            if (database.AddToCushion(deposit, DateOnly.FromDateTime(received.DateTime), id))
            {
                onDeposit?.Invoke(deposit);
            }

            return;
        }

        switch (AlertParser.Read(from, subject, body, DateOnly.FromDateTime(received.DateTime), id))
        {
            case { } purchase:
                // Already counted means already told, so a repeat sends nothing.
                if (database.ImportTransactions(purchase.Account, [purchase.Transaction]).Added > 0)
                {
                    onPurchase(purchase);
                }

                break;
            default:
                if (AlertParser.LooksLikeMoney(from, subject))
                {
                    onUnread(new UnreadAlert(from, subject, body));
                }

                break;
        }
    }

    /// <summary>
    /// The topmost Authentication-Results header, which the receiving server adds, so a sender can't write it.
    /// "" when headers came back without one, and null when Graph didn't send headers at all.
    /// </summary>
    private static string? AuthenticationResults(JsonElement message)
    {
        if (!message.TryGetProperty("internetMessageHeaders", out var headers) || headers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var header in headers.EnumerateArray())
        {
            if (header.TryGetProperty("name", out var headerName)
                && string.Equals(headerName.GetString(), "Authentication-Results", StringComparison.OrdinalIgnoreCase))
            {
                return header.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "";
            }
        }

        return "";
    }

    private async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken cancellationToken, bool allowErrors = false)
    {
        using var response = await http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken);
        if (!allowErrors)
        {
            response.EnsureSuccessStatusCode();
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            // An error page that isn't JSON, such as from a proxy, is reported by its status instead.
            response.EnsureSuccessStatusCode();
            throw;
        }
    }
}

/// <summary>
/// Reads purchase alerts, with the senders and wording the bank profiles give. The alert card's format follows its
/// alert emails. The rewards card's alerts are read by their wording, so anything this isn't sure of is left unread
/// rather than guessed at.
/// </summary>
public static partial class AlertParser
{
    /// <summary>
    /// Whether an unread message is worth keeping to fix the parser. The alert card's second email about
    /// the same purchase and its account notices aren't, since those are read or ignored on purpose. A brokerage
    /// email about a deposit is.
    /// </summary>
    public static bool LooksLikeMoney(string from, string subject)
    {
        var card = BankProfiles.Current.Card;
        var fromCard = AccountFor(from) is { } account
            && (account != card.Name || subject.Equals(card.AlertSubject, StringComparison.OrdinalIgnoreCase))
            && (PurchaseWord().IsMatch(subject) || DepositWord().IsMatch(subject));
        return fromCard || (IsFrom(from, BankProfiles.Current.Brokerage.SenderDomains) && DepositWord().IsMatch(subject));
    }

    // Each profile lists the domains its bank really sends from. A subdomain such as notify.northwind.example.com
    // counts, and a lookalike such as northwind.example.com.evil.test or a gmail address with the bank's name in it doesn't.
    public static bool IsCheckingAlert(string from) => IsFrom(from, BankProfiles.Current.Checking.SenderDomains);

    /// <summary>The domain after the last @, in lower case, or null if the address doesn't have one.</summary>
    public static string? DomainOf(string from)
    {
        var address = from.Trim().TrimEnd('>').Trim();
        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1)
        {
            return null;
        }

        var domain = address[(at + 1)..].TrimEnd('.').ToLowerInvariant();
        return domain.Length > 0 && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? domain : null;
    }

    /// <summary>Whether the address is at one of these domains or a subdomain of one.</summary>
    public static bool IsFrom(string from, IEnumerable<string> domains) =>
        DomainOf(from) is { } domain && domains.Select(allowed => allowed.Trim().TrimEnd('.')).Any(allowed => allowed.Length > 0
            && (domain.Equals(allowed, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Whether the receiving server's Authentication-Results says DMARC passed for the sender's domain.
    /// Null means Graph didn't send headers, so there's nothing to check and the mail is let through.
    /// </summary>
    public static bool PassedDmarc(string? authenticationResults, string from)
    {
        if (authenticationResults is null)
        {
            return true;
        }

        if (!DmarcPass().IsMatch(authenticationResults))
        {
            return false;
        }

        var headerFrom = DmarcHeaderFrom().Match(authenticationResults);
        return !headerFrom.Success || string.Equals(headerFrom.Groups[1].Value.TrimEnd('.'), DomainOf(from), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checking's balance from a checking bank alert: the checking profile's balance wording, then a colon or "is",
    /// then the amount, as in "available balance: $40.00" or "available balance is -$150.00" with the built-in
    /// wording. Null if the email doesn't say.
    /// </summary>
    public static long? ReadAvailableBalance(string body)
    {
        var match = BankProfiles.Current.Checking.AvailableBalancePattern().Match(body);
        if (!match.Success)
        {
            return null;
        }

        var cents = Cents(match.Groups["amount"].Value);
        return match.Groups["minus"].Success ? -cents : cents;
    }

    /// <summary>
    /// Card payments in a checking bank summary, such as "CONTOSO CARD PAYMENT 261101 $450.00", dated by
    /// the six-digit date in the line, or the email's day when there isn't one.
    /// </summary>
    public static IReadOnlyList<(string Account, ImportedTransaction Payment)> ReadCardPayments(string body, DateOnly received)
    {
        var payments = new List<(string, ImportedTransaction)>();
        foreach (Match line in CardPaymentLine().Matches(body))
        {
            var description = line.Groups["text"].Value.Trim();
            if (BankProfiles.Current.CardPaidBy(description, atStart: false)?.Name is not { } account)
            {
                continue;
            }

            var date = ShortDate().Match(description) is { Success: true } digits
                && DateOnly.TryParseExact(digits.Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : received;
            payments.Add((account, new ImportedTransaction(date, description, Cents(line.Groups["amount"].Value))));
        }

        return payments;
    }

    /// <summary>The card an alert is from, by its sender's domain, or null when it isn't one of the cards.</summary>
    public static string? AccountFor(string from) =>
        BankProfiles.Current.Cards.FirstOrDefault(card => IsFrom(from, card.SenderDomains))?.Name;

    /// <summary>The amount of a brokerage deposit confirmation, or null if this isn't one.</summary>
    public static long? ReadDeposit(string from, string subject, string body)
    {
        if (!IsFrom(from, BankProfiles.Current.Brokerage.SenderDomains) || !DepositWord().IsMatch(subject))
        {
            return null;
        }

        var amount = Amount().Match($"{subject}\n{body}");
        return amount.Success ? Cents(amount.Groups[1].Value) : null;
    }

    public static EmailPurchase? Read(string from, string subject, string body, DateOnly received, string messageId)
    {
        var card = BankProfiles.Current.Card;
        return AccountFor(from) switch
        {
            null => null,
            { } account when account == card.Name => ReadCardAlert(card, subject, body, received, messageId),
            { } account => ReadOther(account, subject, body, received, messageId),
        };
    }

    /// <summary>Only the card's alert subject counts. The card sends other emails about the same purchase.</summary>
    private static EmailPurchase? ReadCardAlert(AlertCardProfile card, string subject, string body, DateOnly received, string messageId)
    {
        if (!subject.Trim().Equals(card.AlertSubject, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var merchant = CardAlertMerchant().Match(body);
        var amount = CardAlertAmount().Match(body);
        if (!merchant.Success || !amount.Success)
        {
            return null;
        }

        var date = DateOnly.TryParse(CardAlertDate().Match(body).Groups[1].Value, CultureInfo.InvariantCulture, out var day) ? day : received;
        return new EmailPurchase(card.Name, new ImportedTransaction(date, merchant.Groups[1].Value.Trim(), Cents(amount.Groups[1].Value), messageId));
    }

    private static EmailPurchase? ReadOther(string account, string subject, string body, DateOnly received, string messageId)
    {
        var text = $"{subject}\n{body}";
        if (!PurchaseWord().IsMatch(text) || PaymentWord().IsMatch(subject))
        {
            return null;
        }

        var amount = Amount().Match(text);
        var merchant = Merchant().Match(text);
        return amount.Success && merchant.Success
            ? new EmailPurchase(account, new ImportedTransaction(received, merchant.Groups[1].Value.Trim(), Cents(amount.Groups[1].Value), messageId))
            : null;
    }

    /// <summary>
    /// From the payment app, or forwarded by you from your own address, where the quoted headers name the app's domain.
    /// A forward from anyone else doesn't count, since anyone can type the app's headers into an email.
    /// </summary>
    public static bool IsPaymentApp(string from, string body, string? ownAddress = null)
    {
        var app = BankProfiles.Current.PaymentApp;
        return IsFrom(from, app.SenderDomains)
            || (!string.IsNullOrWhiteSpace(ownAddress) && string.Equals(from.Trim(), ownAddress.Trim(), StringComparison.OrdinalIgnoreCase) && app.ForwardedHeader().IsMatch(body));
    }

    /// <summary>Money going out, or a request for it. Neither is a repayment, and neither is worth keeping.</summary>
    public static bool IsPaymentAppOutgoing(string subject) =>
        BankProfiles.Current.PaymentApp.OutgoingSubject().IsMatch(ForwardPrefix().Replace(subject.Trim(), ""));

    /// <summary>
    /// A best guess at a payment someone sent you, from a subject the payment app profile's receivedPattern matches,
    /// such as "Sam sent you $12.00" with the built-in wording. Anything else from the app is left unread to fix this from.
    /// </summary>
    public static PaymentAppPayment? ReadPaymentApp(string from, string subject, string body, string? ownAddress = null)
    {
        var app = BankProfiles.Current.PaymentApp;
        var paid = app.ReceivedSubject().Match(ForwardPrefix().Replace(subject.Trim(), ""));
        if (!IsPaymentApp(from, body, ownAddress) || !paid.Success
            || !decimal.TryParse(paid.Groups["amount"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var labeled = app.NoteLabel().Match(body);
        var note = labeled.Success ? labeled.Groups[1].Value.Trim() : PaymentAppNoteLine(app, body);
        return new PaymentAppPayment(paid.Groups["payer"].Value.Trim(), Cents(paid.Groups["amount"].Value), string.IsNullOrWhiteSpace(note) ? null : note);
    }

    /// <summary>Without a label, the note is the short line right after the line with the profile's noteAfter wording.</summary>
    private static string? PaymentAppNoteLine(PaymentAppProfile app, string body)
    {
        var lines = body.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var after = app.NoteAfter.Trim();
        var paid = after.Length == 0 ? -1 : lines.FindIndex(line => line.Contains(after, StringComparison.OrdinalIgnoreCase));
        var next = paid >= 0 && paid + 1 < lines.Count ? lines[paid + 1] : null;
        return next is { Length: < 60 } && !app.NotNote().IsMatch(next) ? next : null;
    }

    private static long Cents(string amount) =>
        (long)Math.Round(decimal.Parse(amount.Replace(",", ""), CultureInfo.InvariantCulture) * 100);

    [GeneratedRegex(@"\bdmarc\s*=\s*pass\b", RegexOptions.IgnoreCase)]
    private static partial Regex DmarcPass();

    [GeneratedRegex(@"\bheader\.from\s*=\s*([A-Za-z0-9.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DmarcHeaderFrom();

    [GeneratedRegex(@"^(?:(?:fwd?|re):\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardPrefix();

    [GeneratedRegex(@"Merchant:\s*(.+?)\s*(?:Date:|\r|\n|$)")]
    private static partial Regex CardAlertMerchant();

    [GeneratedRegex(@"Date:\s*([A-Za-z]+ \d{1,2}, \d{4})")]
    private static partial Regex CardAlertDate();

    [GeneratedRegex(@"Amount:\s*\$\s?([\d,]+\.\d{2})")]
    private static partial Regex CardAlertAmount();

    [GeneratedRegex(@"\b(deposit|contribution)", RegexOptions.IgnoreCase)]
    private static partial Regex DepositWord();

    [GeneratedRegex(@"\b(purchase|transaction|charge)", RegexOptions.IgnoreCase)]
    private static partial Regex PurchaseWord();

    [GeneratedRegex(@"\b(payment|statement)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentWord();

    [GeneratedRegex(@"\$\s?([\d,]+\.\d{2})")]
    private static partial Regex Amount();

    [GeneratedRegex(@"^\s*(?<text>[A-Z][^\r\n$]*?PAYMENT[^\r\n$]*?)\s+\$(?<amount>[\d,]+\.\d{2})\s*$", RegexOptions.Multiline)]
    private static partial Regex CardPaymentLine();

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex ShortDate();

    [GeneratedRegex(@"(?:Merchant:?\s+|\bat\s+)([A-Z0-9][^\r\n.,]{1,40}?)(?=\s+(?:was|on|for)\b|[\r\n.,]|$)")]
    private static partial Regex Merchant();
}
