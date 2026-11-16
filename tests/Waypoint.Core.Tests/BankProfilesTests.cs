using Microsoft.Data.Sqlite;
using Waypoint.Core.Budget;
using Waypoint.Core.Data;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

public sealed class BankProfilesTests : IDisposable
{
    private static readonly DateOnly Anchor = new(2026, 10, 1);

    // Names other than the built-in ones, the way an install with its own bank-profiles.json has them.
    private static readonly BankProfiles Own = BankProfiles.Parse("""
        {
          "checking": { "name": "Woodgrove Bank checking", "senderDomains": ["woodgrove.example.com"] },
          "card": { "name": "Litware", "senderDomains": ["litware.example.com"], "paymentDescriptors": ["LITWARE"], "creditDescriptors": ["LITWARE BONUS"] },
          "rewardsCard": { "name": "Proseware Visa", "shortName": "Proseware", "senderDomains": ["proseware.example.com"], "paymentDescriptors": ["PROSEWARE"], "typeColumn": "Kind" },
          "brokerage": { "name": "Adatum", "senderDomains": ["adatum.example.com"], "checkingDescriptors": ["ADATUM INV"] },
          "paymentApp": { "name": "Wingtip", "senderDomains": ["wingtip.example.com"], "checkingDescriptors": ["WINGTIP"] }
        }
        """);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Imports_land_on_the_accounts_the_profile_names()
    {
        using var _ = BankProfiles.Use(Own);
        var database = new WaypointDatabase(_path);
        database.SaveBudgetAccount(new BudgetAccount("Woodgrove Bank checking", AccountKind.Checking, 80000, Anchor));
        database.SaveBudgetAccount(new BudgetAccount("Litware", AccountKind.Card, 30000, Anchor, 250000, 9, 3));
        database.SaveBudgetAccount(new BudgetAccount("Proseware Visa", AccountKind.Card, 5000, Anchor, 100000, 20, 15));

        const string checking = """
            "Date","Details","Amount","Balance"
            "10/08/2026","LITWARE PAYMENT 261008","-150.00","800.00"
            "10/08/2026","PROSEWARE PAYMENT 261008","-25.00","950.00"
            "10/06/2026","CONTOSO CARD PAYMENT 261006","-10.00","975.00"
            "10/05/2026","WINGTIP PAYMENT 261005","-4.00","985.00"
            """;
        var rows = CheckingImport.Parse(checking);
        Assert.Equal(["Litware", "Proseware Visa", null, null], rows.Select(row => row.Card));
        Assert.Equal([CheckingKind.CardPayment, CheckingKind.CardPayment, CheckingKind.Other, CheckingKind.Spending], rows.Select(row => row.Kind));
        database.ImportStatement(checking);

        database.ImportStatement("""
            Posted On,Purchase Date,Category,Description,Amount
            10/03/2026,10/03/2026,Restaurants,CAFE,20.00
            10/04/2026,10/04/2026,Rewards,LITWARE BONUS,-5.00
            """);
        database.ImportStatement("""
            Transaction Date,Description,Amount,Kind
            10/04/2026,BOOKSTORE,-30.00,DEBIT
            """);

        // Nothing new is made: each purchase and payment goes to the account already there under that name.
        var accounts = database.GetCurrentAccounts(new DateOnly(2026, 10, 31));
        Assert.Equal(["Litware", "Proseware Visa", "Woodgrove Bank checking"], accounts.Select(account => account.Name).Order());
        Assert.Equal(30000 - 15000 + 2000 - 500, accounts.Single(account => account.Name == "Litware").BalanceCents);
        Assert.Equal(5000 - 2500 + 3000, accounts.Single(account => account.Name == "Proseware Visa").BalanceCents);

        var spending = database.GetSpending(Anchor, new DateOnly(2026, 10, 31));
        Assert.Equal("Litware", spending.Single(row => row.Title == "CAFE").Source);
        Assert.Equal("Proseware", spending.Single(row => row.Title == "BOOKSTORE").Source);
        Assert.Equal("Checking", spending.Single(row => row.Title == "Wingtip").Source);
    }

    [Fact]
    public void Alerts_count_only_from_the_profile_domains()
    {
        using var _ = BankProfiles.Use(Own);

        Assert.Equal("Litware", AlertParser.AccountFor("alerts@litware.example.com"));
        Assert.Equal("Litware", AlertParser.AccountFor("alerts@Notify.Litware.Example.com"));
        Assert.Equal("Proseware Visa", AlertParser.AccountFor("alerts@proseware.example.com"));
        Assert.Null(AlertParser.AccountFor("alerts@litware.example.com.evil.test"));
        Assert.Null(AlertParser.AccountFor("alerts@notlitware.example.com"));
        Assert.Null(AlertParser.AccountFor("litware.example.com@gmail.com"));
        Assert.Null(AlertParser.AccountFor("alerts@contoso.example.com"));

        var purchase = AlertParser.Read("alerts@litware.example.com", "Purchase alert", "Merchant: CAFE Date: October 3, 2026 Amount: $20.00", Anchor, "m1");
        Assert.Equal("Litware", purchase?.Account);

        Assert.True(AlertParser.IsCheckingAlert("alerts@woodgrove.example.com"));
        Assert.False(AlertParser.IsCheckingAlert("alerts@northwind.example.com"));
        Assert.Equal([("Litware", 45000L)], AlertParser.ReadCardPayments("LITWARE PAYMENT 261101 $450.00", Anchor).Select(found => (found.Account, found.Payment.AmountCents)));

        Assert.Equal(10000, AlertParser.ReadDeposit("alerts@adatum.example.com", "Deposit received", "$100.00"));
        Assert.Null(AlertParser.ReadDeposit("alerts@adatum.example.com.evil.test", "Deposit received", "$100.00"));

        Assert.True(AlertParser.IsPaymentApp("pay@wingtip.example.com", ""));
        Assert.False(AlertParser.IsPaymentApp("pay@wingtip.example.com.evil.test", ""));
        Assert.True(AlertParser.IsPaymentApp("me@example.com", "From: Wingtip <pay@wingtip.example.com>\n", "me@example.com"));
        Assert.False(AlertParser.IsPaymentApp("me@example.com", "From: Wingtip <pay@wingtip.example.com.evil.test>\n", "me@example.com"));
        Assert.False(AlertParser.IsPaymentApp("me@example.com", "From: Tailspin Pay <pay@tailspinpay.example.com>\n", "me@example.com"));
    }

    [Fact]
    public void Transfers_and_short_names_follow_the_profile()
    {
        using var _ = BankProfiles.Use(Own);

        Assert.True(SavingsPlan.IsTransfer("Adatum on the 3rd"));
        Assert.False(SavingsPlan.IsTransfer("Fabrikam on the 3rd"));
        Assert.Equal("Proseware", SpendingRow.SourceOf(SpendingRow.ImportedPrefix + "Proseware Visa"));
        Assert.Equal("Checking", SpendingRow.SourceOf(SpendingRow.ImportedPrefix + "Woodgrove Bank checking"));
        Assert.Equal("Litware", SpendingRow.SourceOf(SpendingRow.ImportedPrefix + "Litware"));
    }

    [Fact]
    public void Statement_wording_follows_the_profile()
    {
        using var _ = BankProfiles.Use(BankProfiles.Parse("""
            {
              "checking": {
                "purchasePrefix": "^DEBIT BUY(?: ON \\d{1,2}-\\d{1,2})?\\s*",
                "referenceSuffix": "\\s+TRACE#\\d+.*$",
                "balanceWording": "balance (?:now|left)"
              },
              "card": { "alertSubject": "Card charge notice", "exportPaymentDescriptors": ["EASY SETTLE"], "csvDateHeaders": ["Swiped On"] }
            }
            """));

        var rows = CheckingImport.Parse("""
            Date,Details,Amount,Balance
            11/02/2026,DEBIT BUY ON 11-1 CORNER MART OAKHAVEN TRACE#123,-8.00,491.00
            11/02/2026,CARD PURCHASE 11/01 CORNER MART REF 123456789,-9.00,499.00
            """);
        Assert.Equal([CheckingKind.Spending, CheckingKind.Other], rows.Select(row => row.Kind));
        Assert.Equal("CORNER MART OAKHAVEN", CheckingImport.PurchaseTitle(rows[0].Description));

        Assert.Equal(-1250, AlertParser.ReadAvailableBalance("Your balance now: -$12.50"));
        Assert.Equal(4000, AlertParser.ReadAvailableBalance("Checking balance left is $40.00 today."));
        Assert.Null(AlertParser.ReadAvailableBalance("Your available balance is $40.00"));

        Assert.True(AlertParser.LooksLikeMoney("alerts@contoso.example.com", "Card charge notice"));
        Assert.Null(AlertParser.Read("alerts@contoso.example.com", "Purchase alert", "Merchant: CAFE Date: October 3, 2026 Amount: $20.00", Anchor, "m1"));
        Assert.Equal("CAFE", AlertParser.Read("alerts@contoso.example.com", "Card charge notice", "Merchant: CAFE Date: October 3, 2026 Amount: $20.00", Anchor, "m1")?.Transaction.Description);

        const string export = """
            Swiped On,Description,Amount,Category
            10/01/2026,EASY SETTLE THANKS,-800.00,Payments
            10/03/2026,CAFE,20.00,Restaurants
            """;
        Assert.True(CsvImport.IsCardExport(export));
        Assert.Equal(80000, CsvImport.ParsePayments(export).Single().AmountCents);
        Assert.Equal((new DateOnly(2026, 10, 3), 2000L), CsvImport.Parse(export).Select(row => (row.Date, row.AmountCents)).Single());
    }

    [Fact]
    public void Payment_app_wording_follows_the_profile()
    {
        using var _ = BankProfiles.Use(BankProfiles.Parse("""
            {
              "paymentApp": {
                "receivedPattern": "^(?<payer>.+?) moved \\$(?<amount>[\\d,]+\\.\\d{2}) to you$",
                "outgoingPattern": "^you moved\\b",
                "noteAfter": "moved",
                "noteLabels": ["Memo"],
                "notNoteWords": ["open the app"]
              }
            }
            """));
        const string from = "notify@tailspinpay.example.com";

        var payment = AlertParser.ReadPaymentApp(from, "Fwd: Sam Lee moved $1,012.50 to you", "Memo: rent");
        Assert.Equal(("Sam Lee", 101250L, "rent"), (payment?.Payer, payment?.Cents, payment?.Note));
        Assert.Equal("tacos", AlertParser.ReadPaymentApp(from, "Sam moved $12.00 to you", "Sam moved $12.00\ntacos\nOpen the app")?.Note);
        Assert.Null(AlertParser.ReadPaymentApp(from, "Sam moved $12.00 to you", "Sam moved $12.00\nOpen the app")?.Note);
        Assert.Null(AlertParser.ReadPaymentApp(from, "Sam sent you $12.00", ""));
        Assert.True(AlertParser.IsPaymentAppOutgoing("You moved $5.00 to Alex"));
        Assert.False(AlertParser.IsPaymentAppOutgoing("You sent Alex $5.00"));
    }

    [Fact]
    public void The_rewards_card_export_is_known_by_the_profiles_type_column()
    {
        const string export = "Date,Description,Amount,Type\n2026-10-04,BOOKSTORE,-30.00,DEBIT\n";
        Assert.True(CsvImport.IsTypeColumnExport(export));
        Assert.Equal(3000, CsvImport.Parse(export).Single().AmountCents);

        using var _ = BankProfiles.Use(Own);
        Assert.False(CsvImport.IsTypeColumnExport(export));
        Assert.Equal(-3000, CsvImport.Parse(export).Single().AmountCents);
        Assert.True(CsvImport.IsTypeColumnExport("\"Date\",\"Description\",\"Amount\",\"KIND\"\n"));
    }

    [Fact]
    public void Statement_wording_that_isnt_a_regular_expression_stops_the_start()
    {
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "checking": { "purchasePrefix": "(unclosed" } }"""));
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "paymentApp": { "outgoingPattern": "(unclosed" } }"""));
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "paymentApp": { "receivedPattern": "(.+) sent you" } }"""));
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "rewardsCard": { "typeColumn": " " } }"""));
    }

    [Fact]
    public void A_profile_keeps_built_in_values_for_what_it_leaves_out()
    {
        var profiles = BankProfiles.Parse("""
            {
              // Only the card's name is changed here.
              "card": { "name": "Litware", },
            }
            """);

        Assert.Equal("Litware", profiles.Card.Name);
        Assert.Equal("Purchase alert", profiles.Card.AlertSubject);
        Assert.Equal(["contoso.example.com"], profiles.Card.SenderDomains);
        Assert.Equal("Northwind Bank checking", profiles.Checking.Name);
        Assert.Equal("Lucerne Card", profiles.RewardsCard.Name);
    }

    [Fact]
    public void A_profile_that_cant_be_used_stops_the_start()
    {
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("{ not json"));
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "card": { "name": "" } }"""));
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "card": { "name": "Same" }, "rewardsCard": { "name": "same" } }"""));
    }

    [Fact]
    public void Without_a_file_the_built_in_profiles_are_used()
    {
        var folder = Directory.CreateTempSubdirectory("waypoint-profiles-").FullName;
        try
        {
            var profiles = BankProfiles.Load(folder);
            Assert.Equal("Northwind Bank checking", profiles.Checking.Name);
            Assert.Equal("Tailspin Pay", profiles.PaymentApp.Name);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Merchant_names_from_the_profile_come_before_the_built_in_ones()
    {
        using var _ = BankProfiles.Use(BankProfiles.Parse("""
            { "merchantNames": [ ["QUICKRIDE", "Quickride"], ["KROGER", "Corner Mart"], ["MEGAMART", "Megamart"] ] }
            """));

        Assert.Equal("Quickride", MerchantName.Clean("QUICKRIDE *TRIP 8005550100"));
        Assert.Equal("Megamart", MerchantName.Clean("MEGAMART #12 OAKHAVEN ZZ"));
        Assert.Equal("Corner Mart", MerchantName.Clean("KROGER #123"));
        Assert.Equal("Blue Plate", MerchantName.Clean("TST*BLUE PLATE Oakhaven ZZ"));
    }

    [Fact]
    public void Merchant_names_without_a_pair_stop_the_start()
    {
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "merchantNames": [ ["QUICKRIDE"] ] }"""));
        Assert.Throws<InvalidDataException>(() => BankProfiles.Parse("""{ "merchantNames": [ ["QUICKRIDE", " "] ] }"""));
        Assert.Equal("Kroger", MerchantName.Clean("KROGER #123"));
    }

    [Fact]
    public void The_example_file_reads_and_uses_only_example_domains()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Waypoint.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        var profiles = BankProfiles.Parse(File.ReadAllText(Path.Combine(folder.FullName, "bank-profiles.example.json")));
        var domains = profiles.Checking.SenderDomains.Concat(profiles.Cards.SelectMany(card => card.SenderDomains))
            .Concat(profiles.Brokerage.SenderDomains).Concat(profiles.PaymentApp.SenderDomains).ToList();
        Assert.NotEmpty(domains);
        Assert.All(domains, domain => Assert.EndsWith(".example.com", domain));

        // The example shows the built-in wording, so copying it changes nothing.
        var builtIn = new PaymentAppProfile();
        Assert.Equal((builtIn.ReceivedPattern, builtIn.OutgoingPattern), (profiles.PaymentApp.ReceivedPattern, profiles.PaymentApp.OutgoingPattern));
        Assert.Equal(new RewardsCardProfile().TypeColumn, profiles.RewardsCard.TypeColumn);
        Assert.All(profiles.MerchantNames, pair => Assert.Equal(2, pair.Count));
    }

    [Fact]
    public void An_older_repayments_table_is_carried_over()
    {
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE app_repayments (message_id TEXT PRIMARY KEY, payer TEXT NOT NULL, cents INTEGER NOT NULL, note TEXT,
                    received_on TEXT NOT NULL, status TEXT NOT NULL, item_id INTEGER);
                INSERT INTO app_repayments VALUES ('m1', 'Sam Lee', 1200, 'tacos', '2026-10-02', 'pending', NULL);
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var database = new WaypointDatabase(_path);

        var waiting = Assert.Single(database.GetWaitingRepayments());
        Assert.Equal(("m1", "Sam Lee", 1200L), (waiting.MessageId, waiting.Payer, waiting.Cents));
    }
}
