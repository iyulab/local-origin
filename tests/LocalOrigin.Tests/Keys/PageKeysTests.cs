using System.Net;
using System.Text;
using LocalOrigin.AspNetCore.Files;
using LocalOrigin.AspNetCore.Keys;
using LocalOrigin.AspNetCore.Storage;
using LocalOrigin.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace LocalOrigin.Tests.Keys;

/// <summary>
/// Page keys on the file channel: a write is taken only with the plain header and the scope's key in the key header — a client
/// that reached the port and sends the plain header alone, or another scope's key, writes nothing. The address carries the key
/// in its fragment, which a browser never sends, and nothing the origin serves contains it. The storage channel's scope-less
/// check refuses to run with keys, and a preview's discard keeps taking the plain header.
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

    private async Task<HttpStatusCode> PutAsync(string scope, string? headerValue, string? keyValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"http://{scope}.localhost/a.json")) { Content = new StringContent("{}", Encoding.UTF8) };
        if (headerValue is not null) request.Headers.Add("X-Local-Origin", headerValue);
        if (keyValue is not null) request.Headers.Add("X-Local-Origin-Key", keyValue);
        using var response = await _app.GetTestClient().SendAsync(request);
        return response.StatusCode;
    }

    private bool Written(string scope) => File.Exists(Path.Combine(_directory, scope, "a.json"));

    [Fact]
    public async Task A_write_with_the_plain_header_and_the_scopes_key_is_taken()
    {
        Assert.Equal(HttpStatusCode.Created, await PutAsync("notes", "1", _keys.KeyOf("notes")));
        Assert.True(Written("notes"));
    }

    [Theory]
    [InlineData("1", null, "the plain header any program can send")]
    [InlineData(null, null, "no header")]
    [InlineData("1", "", "an empty key")]
    [InlineData(null, "key", "the key without the plain header")]
    [InlineData("key", null, "the key in place of the plain header's 1 (the old shape)")]
    public async Task A_write_without_both_headers_is_refused_and_writes_nothing(string? value, string? key, string why)
    {
        var sent = value == "key" ? _keys.KeyOf("notes") : value;
        var sentKey = key == "key" ? _keys.KeyOf("notes") : key;
        Assert.True(HttpStatusCode.Forbidden == await PutAsync("notes", sent, sentKey), why);
        Assert.False(Written("notes"));
    }

    [Fact]
    public async Task Another_scopes_key_does_not_write_here()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync("notes", "1", _keys.KeyOf("board")));
        Assert.False(Written("notes"));
    }

    private static HttpRequest PageRequest(string? key)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers["X-Local-Origin"] = "1";
        if (key is not null) context.Request.Headers["X-Local-Origin-Key"] = key;
        return context.Request;
    }

    [Fact]
    public void A_keyed_storage_channel_checks_the_key_and_refuses_the_scope_less_check()
    {
        var channel = new StorageChannel(new ChannelSessions(), new StorageChannelOptions { Keys = _keys });
        Assert.True(channel.IsFromThePage(PageRequest(_keys.KeyOf("notes")), "notes"));
        Assert.False(channel.IsFromThePage(PageRequest(null), "notes"));
        Assert.False(channel.IsFromThePage(PageRequest(_keys.KeyOf("board")), "notes"));
        Assert.Throws<InvalidOperationException>(() => channel.IsFromThePage(PageRequest(_keys.KeyOf("notes"))));
    }

    [Fact]
    public void A_storage_channel_without_keys_keeps_the_scope_less_check()
    {
        var channel = new StorageChannel(new ChannelSessions());
        Assert.True(channel.IsFromThePage(PageRequest(null)));
        Assert.True(channel.IsFromThePage(PageRequest(null), "notes"));
    }

    [Fact]
    public async Task A_keyed_storage_channels_preview_discard_takes_the_plain_header()
    {
        var channel = new StorageChannel(new ChannelSessions(), new StorageChannelOptions { Keys = _keys });
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers["X-Local-Origin"] = "1";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"tab\":\"t\",\"ops\":[],\"issued\":0}"));
        context.Response.Body = new MemoryStream();
        await channel.DiscardAsync(context);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
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
        Assert.Contains("\"X-Local-Origin-Key\"", script, StringComparison.Ordinal);
        Assert.Contains("\"local-origin-key\"", script, StringComparison.Ordinal);
        Assert.True(System.Text.Ascii.IsValid(script));
        Assert.Throws<ArgumentException>(() => PageKeys.Script("X-Bad\"Header"));
    }

    [Fact]
    public void The_key_header_sits_beside_the_request_header()
    {
        Assert.Equal("X-Example-Key", PageKeys.KeyHeaderOf("X-Example"));
        Assert.Throws<ArgumentException>(() => PageKeys.KeyHeaderOf(""));
    }
}
