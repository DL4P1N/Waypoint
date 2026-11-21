using System.IO.Compression;
using Microsoft.Extensions.Options;
using Waypoint.Core;
using Waypoint.Core.Background;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;

namespace Waypoint.Server;

/// <summary>
/// Backups, restores, and the notes export, run on the server where the database is. Backups are named by file
/// alone, and only copies in the server's own backups folder can be restored, so nothing outside it is ever opened.
/// </summary>
public static class BackupRoutes
{
    /// <summary>The exit code after a restore. It isn't 0, so systemd starts the server again with the restored data.</summary>
    public const int RestartExitCode = 75;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/backups", (WaypointServer server) =>
            server.Database.GetBackups().Select(backup => new BackupChoice(Path.GetFileName(backup.Path), backup.At, backup.BeforeRestore)).ToList());

        api.MapPost("/backups", (WaypointServer server) => new BackupRequest(Path.GetFileName(server.Database.BackUpNow(DateTime.Now))));

        api.MapPost("/backups/restore", async (BackupRequest request, WaypointServer server, ServerMaintenance maintenance, IServiceProvider services, IOptions<ServerOptions> options, IHostApplicationLifetime lifetime, ILoggerFactory logs) =>
        {
            var backup = server.Database.GetBackups().FirstOrDefault(found => Path.GetFileName(found.Path) == request.Name);
            if (backup is null)
            {
                return Results.Ok(new BackupRestored(false, null, "That backup isn't on the server anymore.", false));
            }

            // A wrong file is turned down before anything is stopped, so the server doesn't start again for nothing.
            if (server.Database.RestoreProblem(backup.Path) is { } problem)
            {
                return Results.Ok(new BackupRestored(false, null, problem, false));
            }

            // Changes from the PC are turned away from here on, and the ones under way are waited for. If they don't
            // finish in time, nothing has been stopped yet, so the server just takes changes again.
            if (!maintenance.TryStartRestore())
            {
                return Results.Ok(new BackupRestored(false, null, "A restore is already running on the server.", false));
            }

            // Whatever goes wrong from here, a restore that has started stopping things always ends with the server
            // starting again, and one that hasn't takes changes again, so the 503s never stay on by mistake.
            var log = logs.CreateLogger("Waypoint.Backups");
            var restarting = options.Value.RestartAfterRestore;
            var stopping = false;
            RestoreResult? result = null;
            try
            {
                if (!await maintenance.DrainChangesAsync(ExclusiveRestore.StopLimit))
                {
                    return Results.Ok(new BackupRestored(false, null, "The server was still saving other changes, so nothing was changed. Try the restore again in a moment.", false));
                }

                // Then everything else that writes: the background loops with Discord and the work they handed on,
                // capture sorting, the work started without being waited for, and notes being written. None of it
                // starts again here, so the server always starts again after.
                stopping = true;
                var worker = server.Worker;
                IEnumerable<Func<TimeSpan, Task<bool>>> stops =
                [
                    .. services.GetServices<IHostedService>().OfType<BackgroundLoops>().Select(loops => (Func<TimeSpan, Task<bool>>)loops.StopForRestoreAsync),
                    worker.Captures.StopAsync,
                    async limit =>
                    {
                        await worker.StopDiscordAsync(limit);
                        return true;
                    },
                    limit => worker.StopWorkAsync(limit),
                    async limit => await server.Host.StopAsync(limit) == 0,
                    .. maintenance.Writers,
                ];

                if (!await ExclusiveRestore.StopWritersAsync(stops, ExclusiveRestore.StopLimit, exception => log.LogError(exception, "Stopping for a restore failed")))
                {
                    result = new RestoreResult(false, null, ExclusiveRestore.StillBusy);
                }
                else
                {
                    try
                    {
                        result = server.Database.RestoreBackup(backup.Path, DateTime.Now);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        log.LogError(exception, "Restoring {Backup} failed", backup.Path);
                        result = new RestoreResult(false, null, $"The restore didn't work, so nothing was changed. {PlainProblem.Report(exception)} Try again, or pick another backup.");
                    }
                }

                return Results.Ok(new BackupRestored(result.Restored, result.SafetyCopy is { } copy ? Path.GetFileName(copy) : null, result.Problem, restarting));
            }
            finally
            {
                if (!stopping)
                {
                    maintenance.EndRestore();
                }
                else if (restarting)
                {
                    // Reminders, Discord, and bedtime were read from the old data and are stopped now, so the server
                    // starts again to read the restored copy, or the same one when the restore was given up. The pause
                    // lets this answer reach the PC first.
                    log.LogWarning(result?.Restored == true ? "Restored {Backup}. Restarting to load it." : "Gave up restoring {Backup}. Restarting to start everything again.", backup.Path);
                    _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ =>
                    {
                        Environment.ExitCode = RestartExitCode;
                        lifetime.StopApplication();
                    }, TaskScheduler.Default);
                }
            }
        }).WithMetadata(Api.RestoreRoute.Instance);

        api.MapGet("/export/notes", (WaypointServer server) =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"waypoint-export-{Guid.NewGuid():N}");
            try
            {
                server.Database.ExportNotes(folder);
                Directory.CreateDirectory(folder);
                using var zip = new MemoryStream();
                ZipFile.CreateFromDirectory(folder, zip);
                return Results.File(zip.ToArray(), "application/zip", $"Waypoint notes {DateTime.Now:yyyy-MM-dd HHmm}.zip");
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        });
    }
}
