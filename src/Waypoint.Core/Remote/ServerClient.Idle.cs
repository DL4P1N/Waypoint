using System.Net.Http.Json;

namespace Waypoint.Core.Remote;

public sealed partial class ServerClient
{
    /// <summary>
    /// Tells the server how long since this PC's last input, for tonight's check-ins. False if it didn't get there,
    /// which the server takes as idle once it's been a few minutes, so nothing is retried. It's tried even while the
    /// server seems away, since in Local mode nothing else checks it.
    /// </summary>
    public async Task<bool> ReportIdleAsync(IdleReport report, CancellationToken cancellationToken = default)
    {
        if (!HasToken)
        {
            return false;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync("api/idle", report, RemoteJson.Options, cancellationToken);
            return Settle(response);
        }
        catch (Exception exception) when (exception is ServerBusyException or ServerRefusedException || IsConnectionProblem(exception, cancellationToken))
        {
            return false;
        }
    }
}
