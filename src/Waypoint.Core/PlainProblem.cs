using System.ComponentModel;
using Waypoint.Core.Remote;

namespace Waypoint.Core;

/// <summary>
/// What went wrong, said plainly for the person using Waypoint. An exception's own text is for the log, since it can
/// carry file paths, process output, and words only a programmer reads, so it's only shown when Waypoint wrote it for
/// people, as it does for the server's messages.
/// </summary>
public static class PlainProblem
{
    /// <summary>Where the detail goes: errors.log on the PC and the journal on the server. Nothing when unset.</summary>
    public static Action<Exception>? Log { get; set; }

    /// <summary>Puts the detail in the log and gives back the plain sentence.</summary>
    public static string Report(Exception exception)
    {
        try
        {
            Log?.Invoke(exception);
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            // The log is only for looking into it later. What the person sees matters more.
        }

        return Describe(exception);
    }

    /// <summary>One plain sentence, ending in a period.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        ServerUnavailableException or ServerTimedOutException or ServerBusyException => exception.Message,
        ServerRefusedException => "The server turned that down.",
        UnauthorizedAccessException => "Windows didn't let Waypoint use that file or folder.",
        FileNotFoundException or DirectoryNotFoundException => "A file or folder it needed isn't there anymore.",
        PathTooLongException => "The folder path is too long for Windows.",
        IOException io when IsDiskFull(io) => "The disk is full.",
        IOException => "A file it needed is open in another program or couldn't be reached.",
        HttpRequestException => "The connection didn't go through.",
        TimeoutException or TaskCanceledException => "It took too long.",
        Win32Exception => "Windows couldn't open what it needed.",
        InvalidOperationException when exception.Message.StartsWith("Claude Code", StringComparison.Ordinal) => "Claude couldn't be reached just now.",
        System.Text.Json.JsonException => "What came back couldn't be read.",
        _ => "Something unexpected went wrong.",
    };

    // ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL, in the low word of the HResult.
    private static bool IsDiskFull(IOException exception) => (exception.HResult & 0xFFFF) is 0x27 or 0x70;
}
