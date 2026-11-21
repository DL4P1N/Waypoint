using Waypoint.Core.Capture;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Background;

/// <summary>Connecting to Discord, the one-copy check, and Discord's settings and health.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>
    /// The live connection carries messages, button presses, and slash commands. Polling every 30 seconds is
    /// only for when it's down or can't carry messages, plus a catch-up whenever it comes back.
    /// Does nothing when Discord isn't set up. With the one-copy check on, it connects only once that check says
    /// no other copy is using the bot.
    /// </summary>
    public void StartDiscord()
    {
        _discordWanted = true;
        _discordWantedAt ??= _clock();
        if (HoldsConnections)
        {
            return;
        }

        if (_runner is not null && (!_claimKnown || DiscordRunsOn is not null))
        {
            _ = CheckDiscordClaimAsync();
            return;
        }

        ConnectDiscord();
    }

    /// <summary>
    /// Set by a PC in server mode that has no address yet: Discord never connects here, and Discord settings saved on
    /// the Settings page are kept for the server instead.
    /// </summary>
    public bool HoldsConnections { get; init; }

    /// <summary>The machine that uses the bot instead of this one, or null when this one does or may.</summary>
    public string? DiscordRunsOn { get; private set; }

    /// <summary>How long alerts wait for Discord to connect before they show on this PC instead.</summary>
    public static readonly TimeSpan DiscordConnectGrace = TimeSpan.FromMinutes(2);

    private DateTime? _discordWantedAt;

    /// <summary>
    /// Discord is set up here but hasn't connected yet, as in the first moments after starting. Alerts wait for it
    /// rather than showing on this PC, unless it takes much longer than it should.
    /// </summary>
    private bool WaitingForDiscord =>
        _discordWanted && !HoldsConnections && DiscordSettings is not null && Discord is null && DiscordRunsOn is null
        && _discordWantedAt is { } since && _clock() - since < DiscordConnectGrace;

    /// <summary>Why the one-copy check couldn't be made, until it can.</summary>
    public string? ClaimProblem { get; private set; }

    /// <summary>Whether Discord is working, for the server's heartbeat.</summary>
    public DiscordHealth DiscordHealth { get; } = new();

    /// <summary>How long Discord can fail before the server stops saying it's healthy.</summary>
    public static readonly TimeSpan DiscordTroubleAfter = TimeSpan.FromMinutes(10);

    /// <summary>What's wrong with Discord, once it has failed for longer than <see cref="DiscordTroubleAfter"/>, or null.</summary>
    public string? DiscordTrouble(DateTime now) =>
        DiscordSettings is null || !_discordWanted || DiscordRunsOn is not null ? null : DiscordHealth.Problem(now, DiscordTroubleAfter);

    /// <summary>
    /// Reads and updates the pinned note that says which copy uses the bot, then connects or steps aside to match.
    /// The host runs it every <see cref="DiscordClaim.CheckEvery"/>. While another copy has it, this one sends no
    /// alerts and reads nothing from Discord. If Discord can't be asked the first time, this copy connects anyway,
    /// so a missing permission never silences everything, and says why in Settings.
    /// </summary>
    public async Task CheckDiscordClaimAsync(CancellationToken cancellationToken = default)
    {
        if (HoldsConnections)
        {
            return;
        }

        if (_runner is not { } runner || DiscordSettings is not { } settings)
        {
            DiscordRunsOn = null;
            return;
        }

        var claim = _claim ??= new DiscordClaim(ClaimClient(), settings, _hostName, runner, () => new DateTimeOffset(_clock()));
        var check = await claim.CheckAsync(cancellationToken);
        if (!ReferenceEquals(claim, _claim))
        {
            // Discord was set up again meanwhile, and that starts its own check.
            return;
        }

        ClaimProblem = check.Problem;
        switch (check.State)
        {
            case ClaimState.Elsewhere:
                _claimKnown = true;
                DiscordRunsOn = check.RunningOn;
                if (Discord is not null)
                {
                    Disconnect();
                }

                _host.Changed();
                return;
            case ClaimState.Here:
                _claimKnown = true;
                DiscordRunsOn = null;
                break;
            case ClaimState.Unknown when _claimKnown:
                return;
        }

        if (_discordWanted && Discord is null)
        {
            ConnectDiscord();
            _host.Changed();
        }
    }

    private HttpClient ClaimClient()
    {
        var client = _http();
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private void ConnectDiscord()
    {
        if (DiscordSettings is not { } settings)
        {
            return;
        }

        var client = _http();
        client.Timeout = TimeSpan.FromSeconds(30);
        var bridge = new DiscordBridge(client, _database, settings)
        {
            Clock = _clock,
            StillWorking = IsDiscordWorking,
            Downloads = _downloads(),
            Captured = message => _host.Post(() => OnDiscordMessage(message)),
            AlertActed = action =>
            {
                var reply = _database.ApplyAlertAction(action, _clock(), DefaultSnoozeMinutes);
                _host.Changed();
                return reply;
            },
            Pressed = OnButtonPressed,
            Acted = OnButtonActedAsync,
            Command = OnDiscordCommand,
            Suggest = OnDiscordSuggest,
            Picked = OnViewPicked,
            PressWillReply = HowPressWillReply,
            SpotSaved = (project, stopped, next) =>
            {
                var saved = SaveSpotFromDiscord(project, stopped, next);
                _host.Changed();
                ViewsChanged();
                return saved;
            },
            SpotProject = () => _database.GetActiveSession(_clock())?.TaskTitle,
            OpensSpotForm = OpensSpotFormForLateNight,
            FeedbackGiven = (feature, itemRef) =>
            {
                // A reminder that didn't help was answered, so it isn't held for after this.
                if (feature == Feedback.Reminder)
                {
                    _database.DropAfterThis(itemRef);
                }

                return _database.AddFeedback(feature, itemRef, null, _clock());
            },
            FeedbackNoted = (id, note) => _database.NoteFeedback(id, note),
            Submitted = OnFormSent,
            NotesSent = message => _host.Post(() => OnNotesMessage(message)),
            ThreadStarted = OnThreadStartedAsync,
            ThreadMessage = OnThreadMessageAsync,
            ThreadGone = OnThreadGone,
        };
        bridge.Connected += () =>
        {
            // Posts typed in while the connection was down are read again on the next round.
            _caughtUpAt = null;
            _host.Post(() => _ = PollDiscordAsync(force: true));
        };

        var stop = new CancellationTokenSource();
        var gateway = new DiscordGateway(settings.BotToken, bridge.HandleEventAsync, _logError, bridge.EventArrived);
        Discord = bridge;
        _gateway = gateway;
        _discordStop = stop;
        if (_connectGateway)
        {
            _gatewayRun = Task.Run(() => gateway.RunAsync(stop.Token));
        }

        _ = PollDiscordAsync(force: true);

        // Voice notes kept from before, such as while speech to text wasn't set up, are tried again now that replies can go out.
        _ = ReadHeldVoiceNotesAsync();
    }

    public void StopDiscord()
    {
        _discordWanted = false;
        _discordWantedAt = null;
        Disconnect();
    }

    /// <summary>
    /// Stops Discord and waits, up to <paramref name="limit"/>, for events already read to be finished, so a capture
    /// or press that arrived just before closing is still saved. Anything left is logged by the gateway.
    /// </summary>
    public async Task StopDiscordAsync(TimeSpan limit)
    {
        var run = _gatewayRun;
        StopDiscord();
        try
        {
            await run.WaitAsync(limit).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logError(exception);
        }
    }

    private void Disconnect()
    {
        _discordStop?.Cancel();
        StopVoiceNotes();
        Discord = null;
        _gateway = null;
        _discordStop = null;
        DiscordHealth.Reset();
    }

    /// <summary>Saves Discord values from the Settings page and reconnects with them straight away. A blank token keeps the one there is.</summary>
    public string SaveDiscord(string? ownerUserId, string? captureChannelId, string? alertsChannelId, string? budgetChannelId, string? token, string? notesForumId = null, string? listsForumId = null)
    {
        if (DiscordSetup.SaveIds(_database, ownerUserId, captureChannelId, alertsChannelId, budgetChannelId, notesForumId, listsForumId) is { } problem)
        {
            return problem;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            _discordToken.Save(token.Trim());
        }

        if (HoldsConnections)
        {
            DiscordSettings = DiscordSetup.Combine(_fileDiscord, _discordToken.Load(), _database);
            _database.SetPreference(Remote.PcMode.HeldDiscordKey, "1");
            return Remote.PcMode.HeldDiscordReply;
        }

        StopDiscord();
        _claim = null;
        _claimKnown = false;
        DiscordRunsOn = null;
        DiscordSettings = DiscordSetup.Combine(_fileDiscord, _discordToken.Load(), _database);
        if (DiscordSettings is null)
        {
            return "Saved. Discord needs both a bot token and your user ID before it can connect.";
        }

        StartDiscord();
        return "Saved. Connecting to Discord now.";
    }

    public async Task<string> TestDiscordAsync() =>
        Discord is not { } discord
            ? "Discord isn't set up yet."
            : await discord.SendTestAsync() is { } problem ? $"The test didn't send: {problem}" : "Sent. Check Discord.";

}
