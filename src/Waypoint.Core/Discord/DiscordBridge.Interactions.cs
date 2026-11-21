using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Discord;

/// <summary>Button presses, slash commands, menu picks and pop-up forms, and how each is answered within Discord's three seconds.</summary>
public sealed partial class DiscordBridge
{
    // Interactions read off the connection and waiting their turn, each with its three seconds already counting.
    private readonly ConcurrentDictionary<string, PendingAnswer> _arrived = new();

    /// <summary>
    /// Answers a button press or a slash command. Discord allows three seconds, so a quick answer goes straight back.
    /// One that takes longer is promised first, then filled in when it's ready, so Discord never says the app
    /// didn't respond. A pressed card turns gray with its buttons gone, unless the press hands back a new card.
    /// Anyone but the owner is turned away.
    /// </summary>
    public Task HandleInteractionAsync(JsonElement interaction, Func<string, AlertActionKind, string?> apply, Func<SlashCommand, CommandReply>? command = null) =>
        AnswerInteraction(interaction, apply, command, Arrive(interaction)).Finished;

    /// <summary>
    /// Told about each event as soon as it's read, before it waits its turn behind the ones ahead. An interaction's
    /// promise is timed from here, so a slow press ahead of it can't use up its three seconds.
    /// </summary>
    public void EventArrived(string type, JsonElement data)
    {
        if (type == "INTERACTION_CREATE" && Text(data, "id") is { } id)
        {
            _arrived[id] = Arrive(data);
        }
    }

    /// <summary>
    /// Starts answering in turn. The first task ends once the press has saved what it changes, or says it is only
    /// waiting now, so the next event can go. The second ends once Discord has its answer.
    /// </summary>
    private (Task InOrder, Task Finished) AnswerInteraction(JsonElement interaction, Func<string, AlertActionKind, string?> apply, Func<SlashCommand, CommandReply>? command, PendingAnswer pending)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnlyWaiting() => waiting.TrySetResult();

        // Suggestions can't be promised, so they're always answered at once.
        var work = TypeOf(interaction) == OptionTyped ? Respond(interaction, apply, command, OnlyWaiting) : Task.Run(() => Respond(interaction, apply, command, OnlyWaiting));
        var finished = FinishAsync(interaction, work, pending);
        return (Task.WhenAny(finished, waiting.Task), finished);
    }

    private async Task FinishAsync(JsonElement interaction, Task<Answer?> work, PendingAnswer pending)
    {
        try
        {
            var answer = await work;
            if (pending.Take())
            {
                // In time, so the answer itself goes back and no promise is made.
                if (answer is not null)
                {
                    await CallbackAsync(interaction, Body(answer));
                }

                return;
            }

            // Already promised, or shown as working, so the answer fills that in.
            if (await pending.Promised && answer is { Data: { } late })
            {
                await EditOriginalAsync(interaction, late);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
        }
    }

    /// <summary>
    /// Starts an interaction's three seconds. Make it smaller says it's working on the card at once. A form or a
    /// suggestion can't be promised, so it waits for its answer. Anything else is promised if it isn't answered in time.
    /// </summary>
    private PendingAnswer Arrive(JsonElement interaction)
    {
        var pending = new PendingAnswer();
        try
        {
            var type = TypeOf(interaction);

            // Make it smaller waits on Claude, so the card says so at once and becomes the step when it's ready.
            if (type == ButtonPressed && IsOwnersPress(interaction, SmallerButton))
            {
                pending.Take();
                pending.Promised = PromiseAsync(interaction, Body(new Answer(UpdateMessage, MessageBody(DiscordText.Notice(Activation.SmallerWorking, "")))), TimeSpan.Zero);
                return pending;
            }

            var press = type == ButtonPressed ? HowPressWillBeAnswered(interaction) : PressReply.Update;
            if (type == OptionTyped || press == PressReply.Form)
            {
                return pending;
            }

            // A command's answer then shows only to the owner.
            var replies = type is CommandUsed or FormSent || press == PressReply.Private;
            var promise = Body(new Answer(replies ? DeferredReply : DeferredUpdate, replies ? new { flags = OnlySeenByPresser } : null));
            pending.Promised = PromiseAsync(interaction, promise, DeferAfter, pending);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
        }

        return pending;
    }

    /// <summary>Sends the promise after the wait, unless the answer got there first. True once it's sent.</summary>
    private async Task<bool> PromiseAsync(JsonElement interaction, object body, TimeSpan after, PendingAnswer? race = null)
    {
        try
        {
            if (after > TimeSpan.Zero)
            {
                await Task.Delay(after);
            }

            if (race is not null && !race.Take())
            {
                return false;
            }

            await CallbackAsync(interaction, body);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>One interaction's single callback: whichever of its answer and its promise takes it first sends it.</summary>
    private sealed class PendingAnswer
    {
        private int _taken;

        /// <summary>True once a promise or a working card was sent, false if none was or it failed.</summary>
        public Task<bool> Promised { get; set; } = Task.FromResult(false);

        public bool Take() => Interlocked.Exchange(ref _taken, 1) == 0;
    }

    private static int TypeOf(JsonElement interaction) =>
        interaction.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.Number ? kind.GetInt32() : ButtonPressed;

    private PressReply HowPressWillBeAnswered(JsonElement interaction)
    {
        if (!interaction.TryGetProperty("data", out var data) || Text(data, "custom_id") is not { } id)
        {
            return PressReply.Update;
        }

        // This didn't help keeps the click and opens its form, which can't wait either.
        if (id == SaveSpotButton || id.StartsWith(DidntHelpPrefix, StringComparison.Ordinal))
        {
            return PressReply.Form;
        }

        if (PressWillReply is not { } how)
        {
            return PressReply.Update;
        }

        try
        {
            var message = interaction.TryGetProperty("message", out var pressed) ? Text(pressed, "id") ?? "" : "";
            return how(id, message, PickedValues(data));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return PressReply.Update;
        }
    }

    private static List<string> PickedValues(JsonElement data) =>
        data.TryGetProperty("values", out var picked) && picked.ValueKind == JsonValueKind.Array
            ? picked.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToList()
            : [];

    /// <summary>Fills in an answer that was promised or shown as working, on the message the interaction answered.</summary>
    private async Task EditOriginalAsync(JsonElement interaction, object data)
    {
        if (Text(interaction, "application_id") is not { } application)
        {
            return;
        }

        using var edited = await SendWithLimitsAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}/webhooks/{application}/{Text(interaction, "token")}/messages/@original") { Content = JsonContent.Create(data) };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return request;
        });
        edited.EnsureSuccessStatusCode();
    }

    /// <summary>Whether the owner pressed this button. Anyone else's press goes the usual way and is turned away there.</summary>
    private bool IsOwnersPress(JsonElement interaction, string button)
    {
        if (!interaction.TryGetProperty("data", out var data) || Text(data, "custom_id") != button)
        {
            return false;
        }

        var user = interaction.TryGetProperty("member", out var member) ? member.GetProperty("user") : interaction.TryGetProperty("user", out var direct) ? direct : default;
        return user.ValueKind == JsonValueKind.Object && Text(user, "id") == settings.OwnerUserId;
    }

    /// <summary>
    /// The Save my spot form was sent: what it's for, where it stopped, and what's next, any of them empty. It hands
    /// back the line to show. The form only offers it, and nothing goes to the sorter.
    /// </summary>
    public Func<string?, string?, string?, string>? SpotSaved { get; set; }

    /// <summary>
    /// Any other pop-up form was sent: its ID after <see cref="FormPrefix"/>, and each box's value by its ID. The
    /// reply is what to show.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>, CommandReply>? Submitted { get; set; }

    /// <summary>What the Save my spot form fills in for what it's for, such as the session going now.</summary>
    public Func<string?>? SpotProject { get; set; }

    /// <summary>
    /// A carried button that also opens the Save my spot form, given the action after <see cref="ActionPrefix"/>. It
    /// does its own part first, such as ending tonight's check-ins, and returns true to open the form.
    /// </summary>
    public Func<string, bool>? OpensSpotForm { get; set; }

    /// <summary>
    /// How a press (its ID, the pressed message, and any menu values) will be answered, so a slow one is promised
    /// the right way: a private reply must not be filled in on the pressed message, and a form can't wait.
    /// </summary>
    public Func<string, string, IReadOnlyList<string>, PressReply>? PressWillReply { get; set; }

    /// <summary>How long an answer may take before Discord is told it's coming. Tests shorten it.</summary>
    public TimeSpan DeferAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Any button press, before the fixed handling. A card handed back replaces the pressed one, and null leaves
    /// the press to <see cref="Pressed"/>. Buttons that carry what they act on only come here.
    /// </summary>
    public Func<ButtonPress, Task<ButtonResult?>>? Acted { get; set; }

    /// <summary>Said instead of an error's own text, which is meant for the log and can hold paths or codes.</summary>
    public const string WentWrong = "Something went wrong on Waypoint's side, so nothing changed. Try again in a moment.";

    private sealed record Answer(int Type, object? Data = null);

    private static Dictionary<string, object> Body(Answer answer)
    {
        var body = new Dictionary<string, object> { ["type"] = answer.Type };
        if (answer.Data is { } data)
        {
            body["data"] = data;
        }

        return body;
    }

    private async Task CallbackAsync(JsonElement interaction, object body)
    {
        var url = $"{ApiBase}/interactions/{Text(interaction, "id")}/{Text(interaction, "token")}/callback";
        using var answer = await SendWithLimitsAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return request;
        });
        answer.EnsureSuccessStatusCode();
    }

    private async Task<Answer?> Respond(JsonElement interaction, Func<string, AlertActionKind, string?> apply, Func<SlashCommand, CommandReply>? command, Action? onlyWaiting = null)
    {
        var type = TypeOf(interaction);
        if (!interaction.TryGetProperty("data", out var data))
        {
            return null;
        }

        var user = interaction.TryGetProperty("member", out var member) ? member.GetProperty("user") : interaction.GetProperty("user");
        var isOwner = Text(user, "id") == settings.OwnerUserId;
        var channel = Text(interaction, "channel_id");

        if (type == OptionTyped)
        {
            return new Answer(SuggestionsReply, new { choices = isOwner ? Suggestions(data, channel) : [] });
        }

        if (type == FormSent && IsFeedbackForm(Text(data, "custom_id")))
        {
            return isOwner ? NoteFeedback(Text(data, "custom_id")!, data) : OnlyOwner();
        }

        if (type == FormSent && Text(data, "custom_id") is { } formId && formId.StartsWith(FormPrefix, StringComparison.Ordinal))
        {
            if (Submitted is not { } submitted)
            {
                return null;
            }

            if (!isOwner)
            {
                return OnlyOwner();
            }

            CommandReply answered;
            try
            {
                answered = submitted(formId[FormPrefix.Length..], FormValues(data));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                answered = new CommandReply(DiscordText.Notice("That didn't work", WentWrong, CardTone.Attention), true);
            }

            return new Answer(ReplyToPresser, MessageBody(answered.Card, flags: answered.Private ? OnlySeenByPresser : null));
        }

        if (type == FormSent)
        {
            if (Text(data, "custom_id") != SpotForm || SpotSaved is not { } save)
            {
                return null;
            }

            if (!isOwner)
            {
                return OnlyOwner();
            }

            var values = FormValues(data);
            string saved;
            try
            {
                saved = save(values.GetValueOrDefault("project"), values.GetValueOrDefault("stopped"), values.GetValueOrDefault("next"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                saved = WentWrong;
            }

            return new Answer(ReplyToPresser, MessageBody(DiscordText.Notice(saved, "", CardTone.Finished), flags: OnlySeenByPresser));
        }

        if (type == CommandUsed)
        {
            if (command is null || Text(data, "name") is not { } name)
            {
                return null;
            }

            if (!isOwner)
            {
                return OnlyOwner();
            }

            // With nothing typed, Save my spot opens the same short form as the button on a card.
            if (name is "save-spot" or "savespot" && !Options(data).ContainsKey("stopped"))
            {
                return new Answer(ShowForm, SpotFormBody());
            }

            CommandReply reply;
            try
            {
                reply = command(new SlashCommand(name, Options(data), channel));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                reply = new CommandReply(DiscordText.Notice("That didn't work", WentWrong, CardTone.Attention), true);
            }

            return reply.Form is { } form
                ? new Answer(ShowForm, FormBody(form))
                : new Answer(ReplyToPresser, MessageBody(reply.Card, flags: reply.Private ? OnlySeenByPresser : null));
        }

        var button = Text(data, "custom_id");
        if (button?.StartsWith(ViewPrefix, StringComparison.Ordinal) == true && interaction.TryGetProperty("message", out var view))
        {
            return isOwner ? Pick(button, Text(view, "id")!, data) : OnlyOwner();
        }

        // Save my spot opens its form straight away, and the card stays as it was, so Start is still there after.
        if (button == SaveSpotButton)
        {
            return isOwner ? new Answer(ShowForm, SpotFormBody()) : OnlyOwner();
        }

        if (button?.StartsWith(DidntHelpPrefix, StringComparison.Ordinal) == true)
        {
            return isOwner ? DidntHelp(button) : OnlyOwner();
        }

        AlertActionKind? known = button is not null && Buttons.TryGetValue(button, out var found) ? found : null;
        var carries = button?.StartsWith(ActionPrefix, StringComparison.Ordinal) == true;
        if (button is null || (known is null && !carries) || !interaction.TryGetProperty("message", out var message))
        {
            return null;
        }

        if (!isOwner)
        {
            return OnlyOwner();
        }

        if (carries && SpotSaved is not null && OpensSpot(button[ActionPrefix.Length..]))
        {
            return new Answer(ShowForm, SpotFormBody());
        }

        var messageId = Text(message, "id")!;
        if (Acted is { } acted)
        {
            ButtonResult? result;
            try
            {
                result = await acted(new ButtonPress(button, known, messageId, channel) { OnlyWaiting = onlyWaiting });
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastError = exception.Message;
                result = new ButtonResult(Outcome: WentWrong);
            }

            if (result?.Form is { } form)
            {
                return new Answer(ShowForm, FormBody(form));
            }

            if (result?.Card is { } card)
            {
                return card.Private
                    ? new Answer(ReplyToPresser, MessageBody(card, flags: OnlySeenByPresser))
                    : new Answer(UpdateMessage, MessageBody(card));
            }

            if (result?.Outcome is { } outcome)
            {
                return new Answer(UpdateMessage, Finished(FirstEmbed(message), outcome));
            }
        }

        return known is { } pressed
            ? new Answer(UpdateMessage, Finished(FirstEmbed(message), apply(messageId, pressed) ?? "Already taken care of"))
            : new Answer(UpdateMessage, Finished(FirstEmbed(message), "That button has run out. Try the command again."));
    }

    private bool OpensSpot(string action)
    {
        try
        {
            return OpensSpotForm?.Invoke(action) == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>The Save my spot form: three short boxes, none required, with what's going now filled in.</summary>
    private object SpotFormBody()
    {
        string? project = null;
        try
        {
            project = SpotProject?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
        }

        object Box(string id, string label, int style, string? value = null) => new
        {
            type = ButtonRow,
            components = new object[]
            {
                value is null
                    ? (object)new { type = TextInput, custom_id = id, label, style, required = false, max_length = MaxFieldText }
                    : new { type = TextInput, custom_id = id, label, style, required = false, max_length = MaxFieldText, value = Shorten(value, MaxFieldText) },
            },
        };

        return new
        {
            custom_id = SpotForm,
            title = CardLabels.SaveSpot,
            components = new[]
            {
                Box("project", "What you're working on", ShortText, string.IsNullOrWhiteSpace(project) ? null : project),
                Box("stopped", "Where you stopped", LongText),
                Box("next", "What's next when you come back", ShortText),
            },
        };
    }

    /// <summary>The boxes of a sent form, by their IDs.</summary>
    private static Dictionary<string, string> FormValues(JsonElement data)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!data.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("components", out var boxes) || boxes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var box in boxes.EnumerateArray())
            {
                if (Text(box, "custom_id") is { } id && Text(box, "value") is { } value)
                {
                    values[id] = value;
                }
            }
        }

        return values;
    }

    /// <summary>A view keeps its place. It's drawn again with the change, or left alone if nothing changed.</summary>
    private Answer Pick(string menu, string messageId, JsonElement data)
    {
        var values = PickedValues(data);

        DiscordCard? card;
        try
        {
            card = Picked?.Invoke(menu, messageId, values);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            card = null;
        }

        return card is null ? new Answer(AcknowledgeOnly)
            : card.Private ? new Answer(ReplyToPresser, MessageBody(card, flags: OnlySeenByPresser))
            : new Answer(UpdateMessage, MessageBody(card));
    }

    /// <summary>A pop-up form, kept inside Discord's limits: five boxes, and short titles and labels.</summary>
    private static object FormBody(CardForm form) => new
    {
        custom_id = FormPrefix + form.Id,
        title = Shorten(form.Title, CardForm.MaxTitle),
        components = form.Boxes.Take(CardForm.MaxBoxes).Select(box =>
        {
            var input = new Dictionary<string, object>
            {
                ["type"] = TextInput,
                ["custom_id"] = box.Id,
                ["label"] = Shorten(box.Label, CardForm.MaxLabel),
                ["style"] = box.Long ? LongText : ShortText,
                ["required"] = box.Required,
                ["max_length"] = Math.Clamp(box.MaxLength, 1, MaxFormText),
            };
            if (!string.IsNullOrEmpty(box.Value))
            {
                input["value"] = Shorten(box.Value, Math.Clamp(box.MaxLength, 1, MaxFormText));
            }

            if (box.Placeholder is { Length: > 0 } placeholder)
            {
                input["placeholder"] = Shorten(placeholder, MaxPlaceholder);
            }

            return (object)new { type = ButtonRow, components = new object[] { input } };
        }).ToArray(),
    };

    private object[] Suggestions(JsonElement data, string? channel)
    {
        if (Suggest is null || Text(data, "name") is not { } name || Focused(data) is not { } focused)
        {
            return [];
        }

        try
        {
            return Suggest(new SlashCommand(name, Options(data), channel), focused)
                .Take(MaxSuggestions)
                .Select(choice => (object)new { name = Shorten(choice.Label, MaxChoiceText), value = Shorten(choice.Value, MaxChoiceText) })
                .ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastError = exception.Message;
            return [];
        }
    }

    /// <summary>The option being typed while suggestions are asked for.</summary>
    private static string? Focused(JsonElement data) =>
        data.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(option => option.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True).Select(option => Text(option, "name")).FirstOrDefault()
            : null;

    private static string Shorten(string text, int limit) => DiscordText.Shorten(text, limit);

    private static Answer OnlyOwner() => new(ReplyToPresser, new { content = "Only the owner can do that.", flags = OnlySeenByPresser });

    private static Dictionary<string, string> Options(JsonElement data)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in list.EnumerateArray())
            {
                if (Text(option, "name") is { } name && option.TryGetProperty("value", out var value))
                {
                    options[name] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                }
            }
        }

        return options;
    }

    private static JsonElement? FirstEmbed(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array && embeds.GetArrayLength() > 0 && embeds[0].ValueKind == JsonValueKind.Object ? embeds[0] : null;

    /// <summary>
    /// A handled card keeps what it said, turns gray with its buttons gone, and says what was done underneath.
    /// The outcome leaves out the card's own title, so "Done: Call mom" under "Call mom" reads "Done".
    /// </summary>
    private static object Finished(JsonElement? embed, string outcome)
    {
        var title = embed is { } shown ? Text(shown, "title") : null;
        if (title is null)
        {
            return new { embeds = new[] { new { title = outcome, color = (int)CardTone.Finished } }, components = Array.Empty<object>() };
        }

        var fields = embed!.Value.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array ? list.Clone() : (JsonElement?)null;
        var footer = outcome.EndsWith(": " + title, StringComparison.Ordinal) ? outcome[..^(title.Length + 2)] : outcome;
        var card = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["color"] = (int)CardTone.Finished,
            ["footer"] = new { text = footer },
        };
        if (Text(embed.Value, "description") is { } description)
        {
            card["description"] = description;
        }

        if (fields is { } kept)
        {
            card["fields"] = kept;
        }

        return new { embeds = new[] { card }, components = Array.Empty<object>() };
    }
}
