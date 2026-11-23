using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// The Budget page, its forms and right-click menus, and CSV import, for the PC app in server mode. Each one calls the
/// same database method the page calls in local mode, with the server's clock for today.
/// </summary>
public static class BudgetRoutes
{
    public static void Map(RouteGroupBuilder api)
    {
        var budget = api.MapGroup("/budget");

        budget.MapGet("/page/{month}", (string month, WaypointServer server) =>
            DateOnly.TryParseExact(month, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? Results.Ok(BudgetPage.Read(server.Database, new DateOnly(day.Year, day.Month, 1), Today))
                : Results.BadRequest());

        budget.MapGet("/purchases", (string? words, int? limit, WaypointServer server) =>
            server.Database.SearchPurchases(words, Math.Clamp(limit ?? 20, 1, 100)));

        // An expense, a repayment, and pay received are each kept once per key the PC sent them with.
        budget.MapPost("/expenses", (ExpenseRequest request, HttpContext context, WaypointServer server) =>
            SentOnce.Run(context, server.Database, database => database.AddExpense(request.Title, request.Cents, request.Category, request.Date, DateTime.Now)));
        budget.MapPost("/ops", (BudgetOp op, WaypointServer server) => new TextValue(server.Database.ApplyBudgetOp(op, Today)));
        budget.MapPost("/categories", (CategoryRequest request, WaypointServer server) =>
            server.Database.AddCategory(request.Name, request.MonthlyCents, request.Period));
        budget.MapPost("/deposits", (DepositRequest request, WaypointServer server) =>
            server.Database.AddExpectedDeposit(request.Name, request.Cents, request.At));
        budget.MapPut("/pay", (PayScheduleRequest request, WaypointServer server) =>
            server.Database.SetPaySchedule(request.Pattern, request.Days, request.Cents, request.Next));
        budget.MapPut("/bills", (BillRequest request, WaypointServer server) =>
            server.Database.SetBill(request.Name, request.Cents, request.HowOften, request.Days, request.At, request.Account, Today));
        budget.MapPost("/bills/remove", (TextValue name, WaypointServer server) => server.Database.RemoveBill(name.Value));
        budget.MapPut("/goal", (GoalRequest request, WaypointServer server) => server.Database.SetSavingsGoal(request.Cents, request.By, Today));
        budget.MapDelete("/goal", (WaypointServer server) => server.Database.ClearSavingsGoal());
        budget.MapPost("/scores", (CreditScoreRequest request, WaypointServer server) =>
            server.Database.AddCreditScore(request.Score, request.Source, request.At, request.Note, DateTime.Now));
        budget.MapPost("/pay-received", (AmountRequest request, HttpContext context, WaypointServer server) =>
            SentOnce.Run(context, server.Database, database => database.ConfirmPayTyped(request.Cents, Today)));
        budget.MapPost("/wait-hours", (TextValue hours, WaypointServer server) => server.Database.SetDefaultWaitHours(hours.Value));
        budget.MapGet("/patterns", (WaypointServer server) => server.Database.GetSpendingPatterns(Today));
        budget.MapPost("/afford", (AffordCheckRequest request, WaypointServer server) => server.Database.CheckAffordable(request.Cents, request.Card, Today));

        budget.MapPost("/repayments/match", (RepaymentMatchRequest request, WaypointServer server) =>
            server.Database.MatchRepaymentTo(request.MessageId, request.ItemId));
        budget.MapPost("/repayments/clear", (TextValue messageId, WaypointServer server) =>
            messageId.Value is { Length: > 0 } id ? Results.Ok(server.Database.ClearRepaymentById(id)) : Results.BadRequest());

        budget.MapPost("/purchases/{id:long}/category", (long id, NameRequest request, WaypointServer server) =>
            server.Database.CategorizePurchase(id, request.Name));
        budget.MapPost("/purchases/{id:long}/one-off", (long id, FlagValue oneOff, WaypointServer server) =>
            server.Database.MarkPurchaseOneOff(id, oneOff.Value));
        budget.MapPost("/purchases/{id:long}/repayment", (long id, AmountRequest request, HttpContext context, WaypointServer server) =>
            SentOnce.Run(context, server.Database, database => database.RecordRepayment(id, request.Cents)));
        budget.MapPost("/purchases/{id:long}/delete", (long id, WaypointServer server) => server.Database.DeletePurchase(id, DateTime.Now));
        budget.MapPost("/waits/{id:long}/resolve", (long id, TextValue decision, WaypointServer server) =>
            decision.Value is { Length: > 0 } value ? Results.Ok(server.Database.ResolveWaitItem(id, value)) : Results.BadRequest());

        // The PC picked and read the files. The import itself is the one Discord uses, so the reply reads the same.
        budget.MapPost("/import", (StatementsRequest request, WaypointServer server) =>
            request.Statements is { Count: > 0 } statements
                ? Results.Ok(new TextValue(server.Worker.ImportStatementsReply(statements)))
                : Results.BadRequest());
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}
