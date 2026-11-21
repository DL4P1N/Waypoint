using Microsoft.Extensions.Options;
using Waypoint.Core.Background;
using Waypoint.Core.Discord;

namespace Waypoint.Server;

/// <summary>
/// The PC app's timers, on the server: alerts every 15 seconds, Discord every 30, email every 5 minutes, and the
/// calendar every hour. Each runs on its own, so a slow one never holds up reminders. The heartbeat only pings
/// while the alert checks keep running and Discord, when it's set up, keeps working, so a stuck server or a broken
/// Discord goes quiet and the outside monitor notices.
/// </summary>
public sealed class BackgroundLoops(WaypointServer server, IOptions<ServerOptions> options, IHttpClientFactory http, ILogger<BackgroundLoops> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = server.Worker;
        var loops = new List<Task>
        {
            Every(WaypointWorker.AlertCheckInterval, () =>
            {
                worker.CheckAlerts();
                server.LastCheck = DateTime.Now;
                return Task.CompletedTask;
            }, stoppingToken),
            Once(() => worker.Captures.ProcessPendingAsync(stoppingToken)),
        };

        // A feed can also be set up later from the PC's Settings page, so this runs either way and does nothing
        // until there is one. A link saved from then on is read straight away.
        worker.SyncsCalendar = true;
        loops.Add(Every(WaypointWorker.CalendarSyncInterval, () => worker.SyncCalendarAsync(stoppingToken), stoppingToken));

        // The live connection runs on its own once started, and polling catches up when it can't carry messages.
        // Discord can also be set up later from the PC's Settings page, so the poll runs either way and does
        // nothing until it's connected.
        // Only one copy uses the bot at a time. The server wins over a PC still in Local mode, which steps aside.
        worker.StartDiscord();
        stoppingToken.Register(worker.StopDiscord);
        loops.Add(Every(DiscordClaim.CheckEvery, () => worker.CheckDiscordClaimAsync(stoppingToken), stoppingToken));
        loops.Add(Every(WaypointWorker.DiscordPollInterval, () => worker.PollDiscordAsync(), stoppingToken));

        // Notices that couldn't be sent are tried again until Discord takes them or the PC shows them.
        loops.Add(Every(WaypointWorker.DiscordPollInterval, () => server.Host.RetryNoticesAsync(async (title, text, nonce) =>
        {
            if (worker.Discord is not { } discord)
            {
                return false;
            }

            var sent = (await discord.DeliverOnceAsync(discord.AlertsChannelFor(), DiscordText.Notice(title, text), nonce)).Sent;
            worker.DiscordHealth.Sent(sent, DateTime.Now);
            return sent;
        }), stoppingToken));

        // Alert email can also be set up later from the PC's Settings page, so this runs either way and does nothing until then.
        loops.Add(Every(WaypointWorker.EmailPollInterval, worker.PollEmailAsync, stoppingToken));

        if (options.Value.HeartbeatUrl is { Length: > 0 } heartbeat)
        {
            loops.Add(Every(TimeSpan.FromMinutes(Math.Max(1, options.Value.HeartbeatMinutes)), () => PingAsync(heartbeat, stoppingToken), stoppingToken));
        }

        return Task.WhenAll(loops);
    }

    /// <summary>
    /// After the loops end, Discord events already read are finished, then the work they started without waiting
    /// for it, then the work they handed on, each within a time limit, so a capture or press that came in just before
    /// shutdown is still saved. What's left is logged.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await server.Worker.StopDiscordAsync(DiscordGateway.EventDrainLimit);
        if (!await server.Worker.StopWorkAsync(DiscordGateway.EventDrainLimit))
        {
            logger.LogWarning("Stopped with background work still going");
        }

        await server.Host.StopAsync(OrderedWork.DefaultStopLimit);
    }

    /// <summary>
    /// Stops the loops, Discord, and the work they handed on, the same as shutting down, before a restore. True once
    /// the loops have ended within <paramref name="limit"/>. The host stops it again on the way out, which does nothing more.
    /// </summary>
    public async Task<bool> StopForRestoreAsync(TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        await StopAsync(timeout.Token);
        return ExecuteTask is not { IsCompleted: false };
    }

    private async Task PingAsync(string url, CancellationToken stoppingToken)
    {
        if (server.Problem(DateTime.Now, runsBackground: true) is { } problem)
        {
            logger.LogWarning("The heartbeat is held back: {Problem}", problem);
            return;
        }

        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.GetAsync(url, stoppingToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("The heartbeat answered {Status}", (int)response.StatusCode);
        }
    }

    private Task Once(Func<Task> work) => Task.Run(async () =>
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Background work failed");
        }
    });

    /// <summary>Runs at once and then on each tick. A failure is logged and the loop carries on.</summary>
    private Task Every(TimeSpan interval, Func<Task> work, CancellationToken stoppingToken) => Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await work();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background work failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    });

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
