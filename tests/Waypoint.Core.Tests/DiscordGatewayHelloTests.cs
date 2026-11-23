using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Tests;

/// <summary>A connection where Discord never says hello is dropped and tried again, against a local stand-in with no network.</summary>
public sealed class DiscordGatewayHelloTests
{
    [Fact]
    public async Task A_connection_that_never_gets_a_hello_is_given_up_on_so_it_is_tried_again()
    {
        using var stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref connections);
                _ = AcceptWithoutHelloAsync(client, answered, stop.Token);
            }
        });

        // The wait for the hello runs out only when the test says, once the connection has been answered, so how busy
        // the machine is never decides which comes first.
        var clock = new HandClock();
        var gateway = new DiscordGateway("made-up-token", (_, _) => Task.CompletedTask)
        {
            Address = $"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/",
            Time = clock,
        };
        var run = gateway.RunAsync(stop.Token);
        try
        {
            await answered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var timer = await clock.Made.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(gateway.HelloWait, timer.Due);
            Assert.Null(gateway.LastError);
            Assert.False(gateway.IsConnected);

            timer.Callback(timer.State);
            Assert.True(SpinWait.SpinUntil(() => gateway.LastError is not null, TimeSpan.FromSeconds(20)), "The missing hello was never noticed");
            Assert.Equal(DiscordGateway.NoHello, gateway.LastError);
            Assert.False(gateway.IsConnected);
            Assert.Equal(1, Volatile.Read(ref connections));
        }
        finally
        {
            await stop.CancelAsync();
            await Record.ExceptionAsync(() => run);
            listener.Stop();
            await Record.ExceptionAsync(() => server);
        }
    }

    /// <summary>A clock whose first timer goes off only when the test calls it, and never on its own.</summary>
    private sealed class HandClock : TimeProvider
    {
        private readonly TaskCompletionSource<(TimerCallback Callback, object? State, TimeSpan Due)> _made = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(TimerCallback Callback, object? State, TimeSpan Due)> Made => _made.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _made.TrySetResult((callback, state, dueTime));
            return new StillTimer();
        }

        private sealed class StillTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Completes the WebSocket handshake, then says nothing, the way a stuck connection does.</summary>
    private static async Task AcceptWithoutHelloAsync(TcpClient client, TaskCompletionSource answered, CancellationToken stop)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var key = "";
            while (await reader.ReadLineAsync(stop) is { Length: > 0 } line)
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    key = line["Sec-WebSocket-Key:".Length..].Trim();
                }
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop);
            answered.TrySetResult();
            await Record.ExceptionAsync(() => Task.Delay(Timeout.Infinite, stop));
        }
    }
}
