using System.Net;
using System.Net.Sockets;
using LocalOrigin.AspNetCore;
using LocalOrigin.AspNetCore.Origins;
using LocalOrigin.Origins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalOrigin.Tests.Origins;

/// <summary>
/// A real server on the loopback address with an endpoint of its own; scopes get listeners while it runs, and every
/// request is answered with the scope its port names.
/// </summary>
public sealed class ScopeListenersTests : IAsyncLifetime
{
    private static readonly RememberedPortOptions Quick = new() { Attempts = 2, RetryDelay = TimeSpan.FromMilliseconds(50) };

    private readonly string _directory = Directory.CreateTempSubdirectory("local-origin-listeners-").FullName;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private WebApplication? _app;
    private ScopeListeners _listeners = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private async Task StartAsync(ScopeListenerOptions? options = null)
    {
        _listeners = new ScopeListeners(new PortOrigins(), new PortMemoryFile(Path.Combine(_directory, "ports.json")),
            (options ?? new ScopeListenerOptions()) with { Remembered = Quick, BindTimeout = TimeSpan.FromSeconds(1) });
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Kestrel:Endpoints:own:Url"] = "http://127.0.0.1:0" });
        builder.Configuration.Sources.Add(_listeners);
        _app = builder.Build();
        _app.Run(context => context.Response.WriteAsync(_listeners.Origins.ScopeOf(context.Request) ?? "(none)"));
        await _app.StartAsync();
    }

    private IServer Server => _app!.Services.GetRequiredService<IServer>();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _listeners.Dispose();
        _http.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<string> GetAsync(int port, string host = "127.0.0.1") =>
        _http.GetStringAsync(new Uri($"http://{host}:{port}/"), TestContext.Current.CancellationToken);

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task Each_scope_is_served_on_a_port_of_its_own()
    {
        await StartAsync();

        var notes = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);
        var board = await _listeners.OpenAsync(Server, "board", TestContext.Current.CancellationToken);

        Assert.NotEqual(notes.Port, board.Port);
        Assert.Null(notes.PreviousPort);
        Assert.Equal("notes", await GetAsync(notes.Port));
        Assert.Equal("board", await GetAsync(board.Port));
        Assert.Equal("notes", await GetAsync(notes.Port, "localhost"));
        Assert.Equal(new Uri($"http://127.0.0.1:{notes.Port}/"), _listeners.Origins.OriginOf("notes"));
    }

    [Fact]
    public async Task Opening_an_open_scope_returns_its_port()
    {
        await StartAsync();

        var first = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);
        var again = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);

        Assert.Equal(first.Port, again.Port);
    }

    [Fact]
    public async Task Closing_a_scope_leaves_the_others_serving_and_its_port_remembered()
    {
        await StartAsync();
        var notes = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);
        var board = await _listeners.OpenAsync(Server, "board", TestContext.Current.CancellationToken);

        Assert.True(await _listeners.CloseAsync(Server, "notes", TestContext.Current.CancellationToken));
        Assert.False(await _listeners.CloseAsync(Server, "notes", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(notes.Port));
        Assert.Equal("board", await GetAsync(board.Port));
        Assert.Throws<InvalidOperationException>(() => _listeners.Origins.OriginOf("notes"));

        var reopened = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);
        Assert.Equal(notes.Port, reopened.Port);
        Assert.Null(reopened.PreviousPort);
        Assert.Equal("notes", await GetAsync(notes.Port));
    }

    [Fact]
    public async Task A_scope_keeps_its_port_across_restarts()
    {
        await StartAsync();
        var before = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);
        await _app!.DisposeAsync();
        _listeners.Dispose();

        await StartAsync();
        var after = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);

        Assert.Equal(before.Port, after.Port);
        Assert.Null(after.PreviousPort);
        Assert.Equal("notes", await GetAsync(after.Port));
    }

    [Fact]
    public async Task A_scope_whose_port_stays_taken_moves_and_says_so()
    {
        await StartAsync();
        var before = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);
        await _app!.DisposeAsync();
        _listeners.Dispose();
        using var holder = new TcpListener(IPAddress.Loopback, before.Port);
        holder.Start();

        await StartAsync();
        var after = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);

        Assert.NotEqual(before.Port, after.Port);
        Assert.Equal(before.Port, after.PreviousPort);
        Assert.Equal("notes", await GetAsync(after.Port));
    }

    [Fact]
    public async Task A_port_range_gives_the_first_free_port_in_it()
    {
        var port = FreePort();
        await StartAsync(new ScopeListenerOptions { Ports = (port, port) });

        var notes = await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);

        Assert.Equal(port, notes.Port);
        await Assert.ThrowsAsync<IOException>(() => _listeners.OpenAsync(Server, "board", TestContext.Current.CancellationToken));
        Assert.False(_listeners.Origins.Bound.ContainsKey("board"));
    }

    [Fact]
    public async Task A_request_on_the_host_s_own_port_names_no_scope()
    {
        await StartAsync();
        await _listeners.OpenAsync(Server, "notes", TestContext.Current.CancellationToken);
        var own = new Uri(Server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First()).Port;

        Assert.Equal("(none)", await GetAsync(own));
    }
}
