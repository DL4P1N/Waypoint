using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core.Background;
using Waypoint.Core.Budget;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Files;
using Waypoint.Core.Remote;
using Waypoint.Core.Theme;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>Problems: guarding a check, unhandled exceptions, and the error log.</summary>
public partial class App
{
    private void Guarded(Action check)
    {
        try
        {
            check();
        }
        catch (Exception exception)
        {
            LogError(exception);
        }
    }

    /// <summary>Once running, staying up matters most, since alerts stop if the app does. A failed start still ends it.</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // In server mode a change made while the server is away says so in Ask, and nothing else is wrong. During
        // startup it's a failed start like any other: handled there, the tray and window would never be made, and the
        // copy would sit invisible, holding the single-instance lock.
        if (e.Exception is ServerUnavailableException away && _started)
        {
            e.Handled = true;
            SayInAsk(away.Message);
            return;
        }

        // A click's change goes to the server off the window's thread, so a slow answer lands here. It's said the same
        // way, and kept in the log.
        if (e.Exception is ServerTimedOutException slow && _started)
        {
            LogError(slow);
            e.Handled = true;
            SayInAsk(slow.Message);
            return;
        }

        LogError(e.Exception);
        e.Handled = _started;

        // Kept going, so it's said once in Ask in a plain line. The same problem coming back stays quiet.
        if (_started && _unexpected.FirstTime(e.Exception) is { } line)
        {
            SayInAsk(line);
        }
    }

    /// <summary>
    /// A notice in Ask from the problem handler. It isn't the answer to anything typed, so a "Sorting..." stays. If
    /// saying it fails too, that's only logged, so the handler itself never takes the app down.
    /// </summary>
    private void SayInAsk(string line)
    {
        try
        {
            _itemsWindow?.ShowNotice(line);
        }
        catch (Exception failure)
        {
            LogError(failure);
        }
    }

    private readonly UnexpectedProblems _unexpected = new();

    private string? _loggedSortFailure;

    private void LogSortFailure(string error)
    {
        if (error == _loggedSortFailure)
        {
            return;
        }

        _loggedSortFailure = error;
        LogText($"Sorting failed: {error}");
    }

    private readonly Dictionary<string, string?> _loggedProblems = [];

    /// <summary>The window says these plainly, so the detail is kept here, once each time it changes.</summary>
    private void LogOnce(string what, string? error)
    {
        if (_loggedProblems.TryGetValue(what, out var logged) && logged == error)
        {
            return;
        }

        _loggedProblems[what] = error;
        if (error is not null)
        {
            LogText($"{what} failed: {error}");
        }
    }

    private static void LogError(Exception exception) => LogText(exception.ToString());

    private static void LogText(string text)
    {
        try
        {
            var path = Path.Combine(GetDataDirectory(), ErrorLogFile);
            if (File.Exists(path) && new FileInfo(path).Length > MaxErrorLogBytes)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, $"{DateTime.Now:O}\n{text}\n---\n");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do with it, and the app should keep running.
        }
    }
}

/// <summary>
/// The plain line for a problem nothing else caught, given once for each distinct problem: the same kind thrown from
/// the same place, whatever its message says. The detail is in errors.log either way.
/// </summary>
internal sealed class UnexpectedProblems
{
    /// <summary>Enough to tell problems apart for a long run, without growing forever.</summary>
    private const int MaxRemembered = 100;

    private readonly HashSet<string> _said = [];

    /// <summary>The line to say, or null when this problem was already said.</summary>
    public string? FirstTime(Exception exception)
    {
        var key = $"{exception.GetType().FullName}|{exception.TargetSite?.DeclaringType?.FullName}.{exception.TargetSite?.Name}";
        if (_said.Contains(key))
        {
            return null;
        }

        if (_said.Count >= MaxRemembered)
        {
            _said.Clear();
        }

        _said.Add(key);
        return $"{Waypoint.Core.PlainProblem.Describe(exception)} Waypoint kept going, and the detail is in errors.log.";
    }
}
