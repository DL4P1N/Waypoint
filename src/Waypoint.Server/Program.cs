using Waypoint.Core.Background;
using Waypoint.Core.Platform;
using Waypoint.Core.Remote;
using Waypoint.Server;

var builder = WebApplication.CreateBuilder(args);

// The console only, which systemd puts in the journal. The default host on Windows also writes warnings to the
// Application event log, where a trial run or the tests would leave Discord and sorting messages.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// The heartbeat's address can hold a secret, and HttpClient logs every request's address at Information.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddSystemd();
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
var options = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.WebHost.UseUrls(options.CheckedListen());

builder.Services.ConfigureHttpJsonOptions(json =>
{
    foreach (var converter in RemoteJson.Options.Converters)
    {
        json.SerializerOptions.Converters.Add(converter);
    }
});
builder.Services.AddHttpClient();
builder.Services.AddSingleton(new WorkerParts(
    path => new FileTokenStore(path),
    TextRecognizer: new Waypoint.Core.Files.TesseractTextRecognizer(new Waypoint.Core.Transcription.ChildProcessRunner()),
    Runner: Waypoint.Core.Discord.DiscordRunner.Server));
builder.Services.AddSingleton<ServerHost>();
builder.Services.AddSingleton<ReportedIdle>();
builder.Services.AddSingleton<ReplyLog>();
builder.Services.AddSingleton<WaypointServer>();
builder.Services.AddSingleton<ServerMaintenance>();
if (options.RunBackground)
{
    builder.Services.AddHostedService<BackgroundLoops>();
}

var app = builder.Build();

// Made now rather than on the first request, so the pairing token and any time zone warning show at startup.
app.Services.GetRequiredService<WaypointServer>();
if (options.PublicHttpWarning() is { } publicHttp)
{
    app.Logger.LogWarning("{Warning}", publicHttp);
}

Api.UseToken(app);
Api.Map(app);
ListsRoutes.Map(Api.Protected(app));
HistoryRoutes.Map(Api.Protected(app));
SettingsRoutes.Map(Api.Protected(app));
BackupRoutes.Map(Api.Protected(app));
IdleRoutes.Map(Api.Protected(app));
app.Run();

/// <summary>Named so the API tests can start the server in memory.</summary>
public partial class Program;
