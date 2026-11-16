using System.IO.Compression;
using Microsoft.Extensions.Options;
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

        api.MapPost("/backups/restore", (BackupRequest request, WaypointServer server, IOptions<ServerOptions> options, IHostApplicationLifetime lifetime, ILoggerFactory logs) =>
        {
            var backup = server.Database.GetBackups().FirstOrDefault(found => Path.GetFileName(found.Path) == request.Name);
            if (backup is null)
            {
                return Results.Ok(new BackupRestored(false, null, "That backup isn't on the server anymore.", false));
            }

            var result = server.Database.RestoreBackup(backup.Path, DateTime.Now);
            var restarting = result.Restored && options.Value.RestartAfterRestore;
            if (restarting)
            {
                // Reminders, Discord, and bedtime were read from the old data, so the server starts again to read the
                // restored copy. The pause lets this answer reach the PC first.
                logs.CreateLogger("Waypoint.Backups").LogWarning("Restored {Backup}. Restarting to load it.", backup.Path);
                _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ =>
                {
                    Environment.ExitCode = RestartExitCode;
                    lifetime.StopApplication();
                }, TaskScheduler.Default);
            }

            return Results.Ok(new BackupRestored(result.Restored, result.SafetyCopy is { } copy ? Path.GetFileName(copy) : null, result.Problem, restarting));
        });

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
