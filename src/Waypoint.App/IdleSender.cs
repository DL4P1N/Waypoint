using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>
/// Sends the late-night idle time to the server on its own timer, apart from the rest of the sync, so a slow or
/// failing sync can't hold it up. In Local mode with a server address it has its own client, which reads the
/// pairing token again whenever it changes. Nothing here can stop the app.
/// </summary>
public sealed class IdleSender
{
    private readonly ServerClient _client;
    private readonly IdleReporter _reporter;
    private readonly Func<string?>? _token;
    private string? _lastToken;

    public IdleSender(ServerClient client, IdleReporter reporter, Func<string?>? token = null)
    {
        _client = client;
        _reporter = reporter;
        _token = token;
    }

    /// <summary>
    /// For Local mode: a sender for the server at <paramref name="address"/>, or null if it isn't an http or https
    /// address. The PC's id is used only if it was already made, so none is made just for this.
    /// </summary>
    public static IdleSender? ForAddress(string? address, string dataDirectory, IdleReporter reporter)
    {
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var tokens = new ProtectedTokenStore(Path.Combine(dataDirectory, ServerConnection.TokenFile));
        var client = new ServerClient(new HttpClient { BaseAddress = baseAddress }, null, ServerConnection.ExistingPcId(dataDirectory));
        return new IdleSender(client, reporter, () => tokens.Load());
    }

    public async Task TickAsync(DateTime now)
    {
        try
        {
            if (_token?.Invoke() is { Length: > 0 } token && token != _lastToken)
            {
                _lastToken = token;
                _client.Pair(token);
            }

            if (_reporter.Due(now) is { } idle)
            {
                await _client.ReportIdleAsync(idle);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Only a reading for tonight's check-ins. The server counts a missed one as no reading.
        }
    }
}
