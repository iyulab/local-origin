using System.Net;
using System.Text;
using LocalOrigin.AspNetCore.Files;
using LocalOrigin.AspNetCore.Keys;
using LocalOrigin.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace LocalOrigin.Tests.Keys;

/// <summary>
/// Page keys on the file channel: a write is taken only with the scope's key as the header's value — a client that reached the
/// port and sends the plain header, or another scope's key, writes nothing. The address carries the key in its fragment, which
/// a browser never sends, and nothing the origin serves contains it.
/// </summary>
public sealed class PageKeysTests : IAsyncLifetime
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly string _directory = Directory.CreateTempSubdirectory("local-origin-keys-").FullName;
    private readonly PageKeys _keys = new(Secret);
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        var folders = new Dictionary<string, ScopeFolder>(StringComparer.Ordinal);
        foreach (var scope in new[] { "notes", "board" })
        {
            Directory.CreateDirectory(Path.Combine(_directory, scope));
            folders[scope] = new ScopeFolder(Path.Combine(_directory, scope));
        }

        var channel = new FileChannel(new FileChannelOptions { Keys = _keys, Allows = _ => ValueTask.FromResult(true) });
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Run(context =>
        {
            var scope = context.Request.Host.Host.Split('.')[0];
            return channel.HandleAsync(context, scope, folders[scope]);
        });
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<HttpStatusCode> PutAsync(string scope, string? headerValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"http://{scope}.localhost/a.json")) { Content = new StringContent("{}", Encoding.UTF8) };
        if (headerValue is not null) request.Headers.Add("X-Local-Origin", headerValue);
        using var response = await _app.GetTestClient().SendAsync(request);
        return response.StatusCode;
    }

    private bool Written(string scope) => File.Exists(Path.Combine(_directory, scope, "a.json"));

    [Fact]
    public async Task A_write_with_the_scopes_key_is_taken()
    {
        Assert.Equal(HttpStatusCode.Created, await PutAsync("notes", _keys.KeyOf("notes")));
        Assert.True(Written("notes"));
    }

    [Theory]
    [InlineData("1", "the plain header any program can send")]
    [InlineData(null, "no header")]
    [InlineData("", "an empty value")]
    public async Task A_write_without_the_key_is_refused_and_writes_nothing(string? value, string why)
    {
        Assert.True(HttpStatusCode.Forbidden == await PutAsync("notes", value), why);
        Assert.False(Written("notes"));
    }

    [Fact]
    public async Task Another_scopes_key_does_not_write_here()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync("notes", _keys.KeyOf("board")));
        Assert.False(Written("notes"));
    }

    [Fact]
    public void A_key_is_the_same_from_the_same_secret_and_different_from_another()
    {
        Assert.Equal(_keys.KeyOf("notes"), new PageKeys(Secret).KeyOf("notes"));
        Assert.NotEqual(_keys.KeyOf("notes"), PageKeys.Ephemeral().KeyOf("notes"));
        Assert.NotEqual(_keys.KeyOf("notes"), _keys.KeyOf("board"));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", _keys.KeyOf("notes"));
    }

    [Fact]
    public void A_short_secret_is_refused()
        => Assert.Throws<ArgumentException>(() => new PageKeys(new byte[16]));

    [Theory]
    [InlineData("http://127.0.0.1:5200/app/", "http://127.0.0.1:5200/app/#local-origin-key=")]
    [InlineData("http://127.0.0.1:5200/app/#/board", "http://127.0.0.1:5200/app/#/board&local-origin-key=")]
    [InlineData("http://127.0.0.1:5200/app/#", "http://127.0.0.1:5200/app/#local-origin-key=")]
    public void The_address_carries_the_key_in_its_fragment_and_keeps_the_pages_own(string url, string prefix)
        => Assert.Equal(prefix + _keys.KeyOf("notes"), _keys.Address(url, "notes"));

    [Fact]
    public void The_script_holds_no_key_and_names_the_header_and_the_parameter()
    {
        var script = PageKeys.Script();
        Assert.DoesNotContain(_keys.KeyOf("notes"), script, StringComparison.Ordinal);
        Assert.Contains("\"X-Local-Origin\"", script, StringComparison.Ordinal);
        Assert.Contains("\"local-origin-key\"", script, StringComparison.Ordinal);
        Assert.True(System.Text.Ascii.IsValid(script));
        Assert.Throws<ArgumentException>(() => PageKeys.Script("X-Bad\"Header"));
    }
}
