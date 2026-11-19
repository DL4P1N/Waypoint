using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Discord;

/// <summary>Sending messages through Discord's REST API: building each message, and waiting out rate limits.</summary>
public sealed partial class DiscordBridge
{
    private async Task<string?> SendAsync(string? channel, DiscordCard card) => (await TrySendAsync(channel, card)).MessageId;

    private async Task<DiscordDelivery> TrySendAsync(string? channel, DiscordCard card, string? replyTo = null, string? nonce = null)
    {
        if (channel is null)
        {
            return new DiscordDelivery(null, true);
        }

        try
        {
            using var response = await SendWithLimitsAsync(() =>
            {
                var request = NewRequest(HttpMethod.Post, $"{ApiBase}/channels/{channel}/messages");
                request.Content = JsonContent.Create(WithNonce(MessageBody(card, replyTo), nonce));
                return request;
            });
            response.EnsureSuccessStatusCode();
            LastError = null;

            using var sent = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return new DiscordDelivery(Text(sent.RootElement, "id"), true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return new DiscordDelivery(null, false, (exception as HttpRequestException)?.StatusCode);
        }
    }

    /// <summary>
    /// A post noted before it's made carries its nonce, and Discord is told to keep to it, so a try sent again
    /// within a few minutes hands back the message already made instead of making another.
    /// </summary>
    private static Dictionary<string, object> WithNonce(Dictionary<string, object> body, string? nonce)
    {
        if (nonce is not null)
        {
            body["nonce"] = nonce;
            body["enforce_nonce"] = true;
        }

        return body;
    }

    /// <summary>Item titles can come from the calendar, so nothing sent is allowed to ping anyone.</summary>
    private static Dictionary<string, object> MessageBody(DiscordCard card, string? replyTo = null, int? flags = null)
    {
        var body = new Dictionary<string, object>
        {
            ["embeds"] = new[]
            {
                new
                {
                    title = card.Title,
                    description = card.Description,
                    color = (int)card.Tone,
                    fields = card.Fields.Select(field => new { name = field.Name, value = field.Value, inline = field.Inline }).ToArray(),
                },
            },
            ["components"] = Components(card),
            ["allowed_mentions"] = new { parse = Array.Empty<string>(), replied_user = false },
        };

        if (replyTo is not null)
        {
            // A deleted original shouldn't stop the answer from arriving.
            body["message_reference"] = new { message_id = replyTo, fail_if_not_exists = false };
        }

        if (flags is { } value)
        {
            body["flags"] = value;
        }

        return body;
    }

    private static object[] Components(DiscordCard card)
    {
        var rows = ButtonRowFor(card).ToList();
        // This didn't help keeps a row of its own at the bottom, so paging never pushes it past Discord's five rows.
        var feedback = FeedbackRow(card);
        var room = MaxRows - rows.Count - (card.Menu is { Choices.Count: > 0 } ? 1 : 0) - (feedback is null ? 0 : 1);
        rows.AddRange(card.MoreRows.Where(row => row.Count > 0).Take(Math.Max(0, room)).Select(row => (object)new
        {
            type = ButtonRow,
            components = row.Take(MaxButtons)
                .Select(button => (object)new { type = Button, style = button.Role == ButtonRole.Main ? GreenButton : GrayButton, label = Shorten(button.Label, MaxButtonLabel), custom_id = ActionPrefix + button.Id })
                .ToArray(),
        }));
        if (card.Menu is { Choices.Count: > 0 } menu)
        {
            var options = menu.Choices.Take(MaxMenuChoices).Select(choice => choice.Description is { } description
                ? (object)new { label = Shorten(choice.Label, MaxChoiceText), value = Shorten(choice.Value, MaxChoiceText), description = Shorten(description, MaxChoiceText) }
                : new { label = Shorten(choice.Label, MaxChoiceText), value = Shorten(choice.Value, MaxChoiceText) });
            rows.Add(new { type = ButtonRow, components = new object[] { new { type = Menu, custom_id = menu.CustomId, placeholder = menu.Placeholder, options = options.ToArray() } } });
        }

        if (feedback is not null && rows.Count < MaxRows)
        {
            rows.Add(feedback);
        }

        return rows.ToArray();
    }

    /// <summary>
    /// Every card's buttons in one order and one set of colors: the main action first and green, then anything else,
    /// then Not now to bring it back later, then Skip for nothing more to come, all gray.
    /// </summary>
    private static object[] ButtonRowFor(DiscordCard card)
    {
        var buttons = new List<(ButtonRole Role, string Label, string Id)>();
        if (card.Dose)
        {
            // Took it keeps the time for that day. Not now brings it back once, and Skip lets the day go.
            buttons.Add((ButtonRole.Main, CardLabels.TookIt, DoneButton));
            if (card.MoreTime)
            {
                buttons.Add((ButtonRole.NotNow, CardLabels.NotNow, SnoozeButton));
            }

            buttons.Add((ButtonRole.Skip, CardLabels.Skip, NotNowReminderButton));
        }
        else if (card.Transition)
        {
            // Start is the way in. 10 more minutes brings it back once, and Skip ends it.
            buttons.Add((ButtonRole.Main, "Start", StartButton));
            if (card.HasButtons)
            {
                buttons.Add((ButtonRole.Other, "Done", DoneButton));
            }

            if (card.MoreTime)
            {
                buttons.Add((ButtonRole.NotNow, CardLabels.MoreTime, MoreTimeButton));
            }

            buttons.Add((ButtonRole.Skip, CardLabels.Skip, NotNowReminderButton));
        }
        else if (card.HasButtons)
        {
            buttons.Add((ButtonRole.Main, "Done", DoneButton));
            buttons.Add((ButtonRole.NotNow, CardLabels.NotNow, SnoozeButton));
        }

        if (card.HasStart && !card.Transition)
        {
            buttons.Add((card.HasButtons ? ButtonRole.Other : ButtonRole.Main, "Start", StartButton));
        }

        if (card.HasStartNext)
        {
            buttons.Add((ButtonRole.Main, "Start", StartNextButton));
        }

        if (card.HasSessionButtons)
        {
            buttons.Add((ButtonRole.Main, "Did it", DidItButton));
            buttons.Add((ButtonRole.Other, "Make it smaller", SmallerButton));
            if (card.CanTrial)
            {
                buttons.Add((ButtonRole.Other, "Try 2 minutes", TrialButton));
            }

            buttons.Add((ButtonRole.NotNow, CardLabels.Pause, NotNowButton));
        }

        switch (card.BedtimeButton)
        {
            case AlertActionKind.WindDownStepDone:
                buttons.Add((ButtonRole.Main, card.BedtimeLabel ?? "Done", WindDownStepDoneButton));
                break;
            case AlertActionKind.EveningStepDone:
                buttons.Add((ButtonRole.Main, card.BedtimeLabel ?? "Done", EveningStepDoneButton));
                break;
            case AlertActionKind.SnoozeBedtime:
                buttons.Add((ButtonRole.NotNow, CardLabels.NotNow, SnoozeBedtimeButton));
                break;
        }

        if (card.SaveSpot)
        {
            buttons.Add((ButtonRole.Other, CardLabels.SaveSpot, SaveSpotButton));
        }

        buttons.AddRange(card.Actions.Select(action => (action.Role, action.Label, ActionPrefix + action.Id)));
        var row = buttons
            .OrderBy(button => button.Role)
            .Take(MaxButtons)
            .Select(button => (object)new { type = Button, style = button.Role == ButtonRole.Main ? GreenButton : GrayButton, label = Shorten(button.Label, MaxButtonLabel), custom_id = button.Id })
            .ToArray();
        return row.Length == 0 ? [] : [new { type = ButtonRow, components = row }];
    }

    /// <summary>
    /// Sends one call, waiting out Discord's rate limit and trying again when told to. The request is built fresh
    /// for each try, since a sent request can't be sent again.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithLimitsAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRateLimitTries)
            {
                return response;
            }

            var wait = await RetryAfterAsync(response, cancellationToken);
            if (wait > MaxRateLimitWait)
            {
                return response;
            }

            response.Dispose();
            await Delay(wait, cancellationToken);
        }
    }

    /// <summary>Discord puts the wait in the body in seconds, with a header as backup.</summary>
    public static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("retry_after", out var seconds)
                && seconds.ValueKind == JsonValueKind.Number)
            {
                return TimeSpan.FromSeconds(Math.Max(0, seconds.GetDouble()));
            }
        }
        catch (JsonException)
        {
            // Falls through to the header.
        }

        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return response.Headers.TryGetValues("Retry-After", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var header)
            ? TimeSpan.FromSeconds(header)
            : TimeSpan.FromSeconds(1);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.BotToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }
}
