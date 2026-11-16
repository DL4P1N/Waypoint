using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Remote;

namespace Waypoint.App.Tests;

/// <summary>server-reads.json on the PC, encrypted to the Windows user the same way the pairing token is.</summary>
public sealed class ServerReadsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-reads-{Guid.NewGuid()}");

    public ServerReadsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string ReadsFile => Path.Combine(_folder, "server-reads.json");

    /// <summary>The app's own protection, the one ServerConnection hands the client.</summary>
    private static IFileProtection UserProtection =>
        (IFileProtection)typeof(UndoBar).Assembly.GetType("Waypoint.App.UserProtection")!.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Up { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up)
            {
                throw new HttpRequestException("No route to host");
            }

            var body = request.RequestUri!.AbsolutePath == "/api/items/open"
                ? JsonSerializer.Serialize(new[] { new StoredItem(1, ItemKind.Task, "Email the landlord", null, null, false) }, RemoteJson.Options)
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static ServerClient Client(FakeServer fake) => new(new HttpClient(fake) { BaseAddress = new Uri("http://server/") }, "token");

    [Fact]
    public void The_last_reads_are_encrypted_to_this_windows_user_and_read_back_after_a_restart()
    {
        var fake = new FakeServer();
        var before = Client(fake);
        before.SaveReadsIn(ReadsFile, UserProtection);
        new ServerToDoData(before).GetOpenItems();

        var stored = File.ReadAllBytes(ReadsFile);
        Assert.DoesNotContain("Email the landlord", Encoding.UTF8.GetString(stored));
        Assert.Contains("Email the landlord", Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(stored, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)));

        fake.Up = false;
        var after = Client(fake);
        after.SaveReadsIn(ReadsFile, UserProtection);
        Assert.Equal("Email the landlord", Assert.Single(new ServerToDoData(after).GetOpenItems()).Title);
    }

    [Fact]
    public void A_plain_file_from_before_is_encrypted_on_the_next_start()
    {
        var fake = new FakeServer();
        var plain = Client(fake);
        plain.SaveReadsIn(ReadsFile);
        new ServerToDoData(plain).GetOpenItems();
        Assert.Contains("Email the landlord", File.ReadAllText(ReadsFile));

        fake.Up = false;
        var after = Client(fake);
        after.SaveReadsIn(ReadsFile, UserProtection);

        Assert.Equal("Email the landlord", Assert.Single(new ServerToDoData(after).GetOpenItems()).Title);
        Assert.DoesNotContain("Email the landlord", Encoding.UTF8.GetString(File.ReadAllBytes(ReadsFile)));
    }
}
