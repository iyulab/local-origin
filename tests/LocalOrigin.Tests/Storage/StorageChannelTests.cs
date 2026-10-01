using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalOrigin.AspNetCore.Storage;
using LocalOrigin.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace LocalOrigin.Tests.Storage;

/// <summary>
/// A host with two scopes, <c>notes</c> and <c>board</c>, on subdomain origins. <c>GET /</c> serves a page
/// (the storage channel's script and a cookie); <c>POST</c> to the channel path writes to the scope's store.
/// </summary>
public sealed partial class StorageChannelTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("local-origin-channel-").FullName;
    private readonly Dictionary<string, KeyValueStore> _stores = new(StringComparer.Ordinal);
    private readonly List<(string Scope, int Count)> _applied = [];
    private readonly List<(string Scope, long Last, int Unapplied)> _refused = [];
    private WebApplication _app = null!;
    private StorageChannel _channel = null!;

    public async ValueTask InitializeAsync()
    {
        foreach (var scope in new[] { "notes", "board" })
            _stores[scope] = await KeyValueStore.OpenAsync(Path.Combine(_directory, scope));

        _channel = new StorageChannel(new ChannelSessions(), new StorageChannelOptions
        {
            Applied = (_, scope, count) =>
            {
                lock (_applied) _applied.Add((scope, count));
                return ValueTask.CompletedTask;
            },
            RefusedFromRetiredTab = refused =>
            {
                lock (_refused) _refused.Add((refused.Scope, refused.Tab.LastSequence, refused.Operations.Count));
                return ValueTask.CompletedTask;
            },
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Run(async context =>
        {
            var scope = context.Request.Host.Host.Split('.')[0];
            if (!_stores.TryGetValue(scope, out var store))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (context.Request.Path == _channel.Options.Path)
            {
                await _channel.HandleAsync(context, scope, _ => ValueTask.FromResult(store));
                return;
            }

            var page = _channel.Open(context, scope, store.GetItems());
            await context.Response.WriteAsync("<!doctype html><script>" + page.Script + "</script>");
        });
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        foreach (var store in _stores.Values) await store.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed record Page(string Tab, string Cookie, string Body);

    private async Task<Page> LoadAsync(string scope)
    {
        using var client = _app.GetTestClient();
        using var response = await client.GetAsync(new Uri($"http://{scope}.localhost/"));
        var body = await response.Content.ReadAsStringAsync();
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var tab = TabPattern().Match(body).Groups[1].Value;
        return new Page(tab, cookie.Split(';')[0], body);
    }

    private async Task<HttpResponseMessage> WriteAsync(string scope, Page page, object batch, bool header = true, string? origin = null)
    {
        var client = _app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://{scope}.localhost{_channel.Options.Path}"))
        {
            Content = JsonContent.Create(batch),
        };
        request.Headers.Add("Cookie", page.Cookie);
        if (header) request.Headers.Add("X-Local-Origin", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    private static object Batch(string tab, params object[] ops) => new { tab, ops };

    private static object Set(long seq, string key, string value) => new { seq, op = "set", key, value };

    private static async Task<long> AckOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("ack").GetInt64();
    }

    [GeneratedRegex("\"tab\":\"([0-9a-f]{32})\"")]
    private static partial Regex TabPattern();

    [Fact]
    public async Task Writes_are_journaled_and_seed_the_next_page()
    {
        var page = await LoadAsync("notes");

        Assert.Equal(2, await AckOf(await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1"), Set(2, "b", "한")))));

        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "한" }, _stores["notes"].GetItems());
        Assert.Equal([("notes", 2)], _applied);
        var next = await LoadAsync("notes");
        Assert.Contains("\"a\":\"1\"", next.Body, StringComparison.Ordinal);
        Assert.Contains("\"b\":\"\\uD55C\"", next.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resending_a_batch_does_not_apply_it_twice()
    {
        var page = await LoadAsync("notes");
        await WriteAsync("notes", page, Batch(page.Tab, Set(1, "n", "1")));

        Assert.Equal(2, await AckOf(await WriteAsync("notes", page, Batch(page.Tab, Set(1, "n", "1"), Set(2, "n", "2")))));
        Assert.Equal(2, await AckOf(await WriteAsync("notes", page, Batch(page.Tab, Set(1, "n", "1"), Set(2, "n", "2")))));

        Assert.Equal(2, _stores["notes"].Sequence);
        Assert.Equal("2", _stores["notes"].GetItems()["n"]);
    }

    [Fact]
    public async Task A_write_without_the_request_header_is_refused()
    {
        var page = await LoadAsync("notes");

        var response = await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1")), header: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stores["notes"].GetItems());
    }

    [Fact]
    public async Task A_write_from_another_origin_is_refused()
    {
        var page = await LoadAsync("notes");

        var response = await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1")), origin: "http://board.localhost");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stores["notes"].GetItems());
    }

    [Fact]
    public async Task One_scopes_session_cannot_write_to_another()
    {
        var notes = await LoadAsync("notes");

        var response = await WriteAsync("board", notes, Batch(notes.Tab, Set(1, "a", "1")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stores["board"].GetItems());
    }

    [Fact]
    public async Task Malformed_operations_are_rejected_whole()
    {
        var page = await LoadAsync("notes");

        var response = await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1"), new { seq = 2, op = "rename" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_stores["notes"].GetItems());
    }

    [Fact]
    public async Task What_a_page_issued_and_that_it_left_are_kept_on_its_tab()
    {
        var page = await LoadAsync("notes");
        await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1")));

        Assert.Equal(1, await AckOf(await WriteAsync("notes", page, new { tab = page.Tab, ops = Array.Empty<object>(), issued = 3, left = true })));

        var tab = _channel.Sessions.Find("notes", page.Tab)!;
        Assert.Equal(1, tab.LastSequence);
        Assert.Equal(3, tab.Issued);
        Assert.True(tab.Left);
        Assert.Null(_channel.Sessions.Find("board", page.Tab));
    }

    [Fact]
    public async Task After_a_revoke_a_page_cannot_write_and_the_host_is_told_of_unapplied_writes()
    {
        var page = await LoadAsync("notes");
        await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1")));

        _channel.Sessions.Revoke("notes");

        Assert.Equal(HttpStatusCode.Forbidden, (await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1")))).StatusCode);
        Assert.Empty(_refused); // a resend of what was applied is no loss
        Assert.Equal(HttpStatusCode.Forbidden, (await WriteAsync("notes", page, Batch(page.Tab, Set(1, "a", "1"), Set(2, "a", "2")))).StatusCode);
        Assert.Equal([("notes", 1L, 1)], _refused); // only what was never applied is handed over
        Assert.Equal("1", _stores["notes"].GetItems()["a"]);
    }

    [Fact]
    public async Task Discarded_writes_are_acknowledged_and_kept_nowhere()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers["X-Local-Origin"] = "1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"tab":"preview","ops":[{"seq":4,"op":"set","key":"a","value":"1"},{"seq":5,"op":"clear"}]}"""));
        context.Response.Body = new MemoryStream();

        await _channel.DiscardAsync(context);

        context.Response.Body.Position = 0;
        Assert.Equal("""{"ack":5}""", await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Empty(_stores["notes"].GetItems());
    }

    [Fact]
    public void The_script_is_ascii_and_stored_values_cannot_close_its_element()
    {
        var script = _channel.Script("t", new Dictionary<string, string> { ["k"] = "</script><script>alert(1)</script>", ["한"] = "\u2028" });

        Assert.True(Ascii.IsValid(script));
        Assert.DoesNotContain("</script", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__LOCAL_ORIGIN_BOOT__", script, StringComparison.Ordinal);
        Assert.Contains("\"handle\":\"__localOrigin\"", script, StringComparison.Ordinal);
        Assert.Contains("\"endpoint\":\"/.local-origin/storage\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_names_the_wire_and_the_handle()
    {
        var channel = new StorageChannel(new ChannelSessions(), new StorageChannelOptions { Path = "/x/storage", RequestHeader = "X-Example", HandleName = "__example" });

        var script = channel.Script("t", new Dictionary<string, string>());

        Assert.Contains("\"endpoint\":\"/x/storage\"", script, StringComparison.Ordinal);
        Assert.Contains("\"header\":\"X-Example\"", script, StringComparison.Ordinal);
        Assert.Contains("\"handle\":\"__example\"", script, StringComparison.Ordinal);
    }
}
