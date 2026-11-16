using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.App;

/// <summary>
/// Runs the notes folder sync off the UI thread: at start, shortly after anything in the folder changes, and every
/// few seconds so edits made in Waypoint reach their files. Only one sync runs at a time. In server mode the notes
/// are the server's, checked less often, and nothing is synced while the server is away, so a sync never acts on
/// old notes. Edits to the files meanwhile are found and brought in once it's back.
/// </summary>
internal sealed class NoteFileSync : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    private readonly INoteFileStore _database;
    private readonly Func<bool> _canSync;
    private readonly Action _notesChanged;
    private readonly Action<Exception> _failed;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _every;
    private NoteFiles _files;
    private FileSystemWatcher? _watcher;
    private bool _running;
    private bool _held;
    private bool _again;
    private string? _lastProblem;

    public NoteFileSync(INoteFileStore database, string root, Action notesChanged, Action<Exception> failed, TimeSpan? every = null, Func<bool>? canSync = null)
    {
        _database = database;
        _canSync = canSync ?? (() => true);
        _files = new NoteFiles(database, root);
        _notesChanged = notesChanged;
        _failed = failed;
        _settle = new DispatcherTimer { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Run();
        };
        _every = new DispatcherTimer { Interval = every ?? Every };
        _every.Tick += (_, _) => Run();
    }

    public string Root => _files.Root;

    public void Start()
    {
        Watch();
        _every.Start();
        Run();
    }

    /// <summary>
    /// Uses a different folder from now on. Every note is written there, and its files come in too. In server mode
    /// it needs the server, and throws while it's away, leaving the old folder in use. <paramref name="moved"/> is
    /// told only once the move is made, so the folder is never saved as the place the notes went when they didn't.
    /// </summary>
    public void MoveTo(string root, Action<string>? moved = null)
    {
        if (!_canSync())
        {
            throw new ServerUnavailableException("Can't reach the server right now, so the notes folder can't move yet. Try again once it's back.");
        }

        _database.ClearNoteFileRecords();
        _files = new NoteFiles(_database, root);
        moved?.Invoke(root);
        Watch();
        Run();
    }

    /// <summary>
    /// Runs a restore with no sync going, waiting for one already running, so no sync acts on the notes from
    /// before it. The next sync then writes the restored notes over the folder.
    /// </summary>
    public T WhileHeld<T>(Func<T> restore)
    {
        _held = true;
        _every.Stop();
        _settle.Stop();
        try
        {
            var result = default(T)!;
            _files.WhileHeld(() => result = restore());
            return result;
        }
        finally
        {
            Resume();
        }
    }

    /// <summary>The same, for a restore on the server, which this PC waits on without holding up the window.</summary>
    public async Task<T> WhileHeldAsync<T>(Func<Task<T>> restore)
    {
        _held = true;
        _every.Stop();
        _settle.Stop();
        try
        {
            while (_running)
            {
                await Task.Delay(50);
            }

            return await restore();
        }
        finally
        {
            Resume();
        }
    }

    private void Resume()
    {
        _held = false;
        _every.Start();
        Run();
    }

    /// <summary>Stops watching and syncs once more, so a note saved just before closing reaches its file.</summary>
    public void Finish()
    {
        Dispose();
        if (!_canSync())
        {
            return;
        }

        try
        {
            _files.Sync(DateTime.Now);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Report(exception);
        }
    }

    public void Dispose()
    {
        _every.Stop();
        _settle.Stop();
        _watcher?.Dispose();
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            Directory.CreateDirectory(Root);
            var watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => Soon();
            watcher.Created += (_, _) => Soon();
            watcher.Deleted += (_, _) => Soon();
            watcher.Renamed += (_, _) => Soon();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The timer still syncs, so outside edits arrive within a few seconds anyway.
            Report(exception);
        }
    }

    /// <summary>Waits for a burst of changes to finish, as an editor saving a file often writes it more than once.</summary>
    private void Soon() => _dispatcher.BeginInvoke(() =>
    {
        _settle.Stop();
        _settle.Start();
    });

    private async void Run()
    {
        if (_running)
        {
            _again = true;
            return;
        }

        if (_held || !_canSync())
        {
            return;
        }

        _running = true;
        try
        {
            var files = _files;
            var result = await Task.Run(() => files.Sync(DateTime.Now));
            _lastProblem = null;

            // Notes kept because their files looked swapped or emptied, not deleted, are logged so it can be looked into.
            if (result.Notice is { } notice)
            {
                Report(new IOException(notice));
            }

            if (result.NotesChanged)
            {
                _notesChanged();
            }
        }
        catch (ServerUnavailableException)
        {
            // The server went away partway. Whatever didn't finish is found again on the next sync.
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Report(exception);
        }
        finally
        {
            _running = false;
        }

        if (_again)
        {
            _again = false;
            Run();
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException
            or ServerUnavailableException or InvalidOperationException or System.Text.Json.JsonException;

    /// <summary>A problem that keeps happening, such as a folder that can't be written to, is logged once.</summary>
    private void Report(Exception exception)
    {
        if (exception.Message != _lastProblem)
        {
            _lastProblem = exception.Message;
            _failed(exception);
        }
    }
}
