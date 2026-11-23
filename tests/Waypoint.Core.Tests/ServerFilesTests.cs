using System.Net;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>Files kept with notes in server mode: a problem with the file here isn't the server being away.</summary>
public sealed class ServerFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-files-{Guid.NewGuid()}");

    public ServerFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Takes any file and gives back a few made-up bytes.</summary>
    private sealed class FileServer : HttpMessageHandler
    {
        public int Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        }
    }

    private readonly FileServer _server = new();

    private ServerClient Client() => new(new HttpClient(_server) { BaseAddress = new Uri("http://server/") }, "token");

    [Fact]
    public async Task A_file_here_that_cant_be_opened_isnt_sent_and_the_server_isnt_taken_to_be_away()
    {
        var client = Client();

        var result = await client.UploadAsync("api/notes/1/sources/2/file", Path.Combine(_folder, "gone.wav"));

        Assert.Equal(FileSendResult.NotRead, result);
        Assert.Equal(0, _server.Asked);
        Assert.True(client.IsOnline);
        Assert.NotEqual(ServerState.Offline, client.State);
    }

    [Fact]
    public async Task A_file_that_cant_be_saved_here_isnt_fetched_and_the_server_isnt_taken_to_be_away()
    {
        var client = Client();
        var notAFolder = Path.Combine(_folder, "taken.txt");
        await File.WriteAllTextAsync(notAFolder, "made up");

        var fetched = await client.DownloadAsync("api/notes/1/sources/2/file", Path.Combine(notAFolder, "inside", "audio.wav"));

        Assert.False(fetched);
        Assert.Equal(1, _server.Asked);
        Assert.Equal(ServerState.Online, client.State);
    }

    [Fact]
    public async Task A_file_fetched_is_saved_whole()
    {
        var client = Client();
        var target = Path.Combine(_folder, "kept", "audio.wav");

        Assert.True(await client.DownloadAsync("api/notes/1/sources/2/file", target));

        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task A_file_sent_goes_whole()
    {
        var client = Client();
        var source = Path.Combine(_folder, "audio.wav");
        await File.WriteAllBytesAsync(source, [9, 8, 7]);

        Assert.Equal(FileSendResult.Sent, await client.UploadAsync("api/notes/1/sources/2/file", source));
    }
}
