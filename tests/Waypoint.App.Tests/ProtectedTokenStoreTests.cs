using System.IO;
using Xunit;

namespace Waypoint.App.Tests;

public sealed class ProtectedTokenStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-tokenstore-{Guid.NewGuid()}");

    public ProtectedTokenStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Saving_over_an_old_token_replaces_it_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_folder, "token.bin");
        var store = new ProtectedTokenStore(path);

        store.Save("first-made-up-token");
        store.Save("second-made-up-token");

        Assert.Equal("second-made-up-token", store.Load());
        Assert.Equal([path], Directory.GetFiles(_folder));
    }
}
