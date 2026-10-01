using System.Net;
using LocalOrigin.AspNetCore;
using LocalOrigin.AspNetCore.Origins;
using LocalOrigin.Origins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalOrigin.Tests.Serving;

/// <summary>
/// A host with its own route (<c>/api/secret</c>) and one scope, <c>notes</c>, in front of it. Scope traffic is answered by
/// the scope handler only; the host's route is reachable from the host's own name only.
/// </summary>
public sealed class ScopeOriginsTests : IAsyncLifetime
{
    private sealed class Strategy : IOriginStrategy
    {
        public bool OwnsAllPorts { get; set; }
        public Uri OriginOf(string scope) => new($"http://{scope}.localhost/");
        public string? ScopeOf(string hostName, int localPort) => hostName == "notes.localhost" ? "notes" : null;
        public bool OwnsPort(int localPort) => OwnsAllPorts;
    }

    private readonly Strategy _strategy = new();
    private readonly List<string> _handled = [];
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseScopeOrigins(_strategy, (context, scope) =>
        {
            lock (_handled) _handled.Add($"{scope} {context.Request.Path}");
            return context.Response.WriteAsync($"scope {scope}");
        });
        _app.MapGet("/api/secret", () => "host secret");
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task<HttpResponseMessage> GetAsync(string host, string path, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{host}{path}"));
        configure?.Invoke(request);
        return await _app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_scope_request_is_answered_by_the_scope_handler_with_the_profile()
    {
        using var response = await GetAsync("notes.localhost", "/index.html");

        Assert.Equal("scope notes", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal(OriginSecurityProfile.DefaultContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task No_host_route_is_reachable_from_a_scope_origin()
    {
        using var response = await GetAsync("notes.localhost", "/api/secret");

        Assert.Equal("scope notes", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["notes /api/secret"], _handled);
    }

    [Fact]
    public async Task The_hosts_own_traffic_passes_untouched()
    {
        using var response = await GetAsync("localhost", "/api/secret");

        Assert.Equal("host secret", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.Contains("Cross-Origin-Resource-Policy"));
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task A_request_another_site_makes_is_refused_before_the_handler_runs()
    {
        using var response = await GetAsync("notes.localhost", "/data.json", r =>
        {
            r.Headers.Add("Sec-Fetch-Site", "cross-site");
            r.Headers.Add("Sec-Fetch-Mode", "cors");
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task On_a_port_the_strategy_owns_a_request_naming_no_scope_reaches_neither_a_scope_nor_the_host()
    {
        _strategy.OwnsAllPorts = true;

        using var response = await GetAsync("rebound.example", "/api/secret");

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task On_a_real_listener_a_scope_port_answers_its_scope_and_refuses_a_rebound_name()
    {
        var directory = Directory.CreateTempSubdirectory("local-origin-scope-origins-").FullName;
        var origins = new PortOrigins();
        using var listeners = new ScopeListeners(origins, new PortMemoryFile(Path.Combine(directory, "ports.json")));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Kestrel:Endpoints:own:Url"] = "http://127.0.0.1:0" });
        builder.Configuration.Sources.Add(listeners);
        await using var app = builder.Build();
        app.UseScopeOrigins(origins, (context, scope) => context.Response.WriteAsync($"scope {scope}"));
        app.MapGet("/api/secret", () => "host secret");
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var port = (await listeners.OpenAsync(app.Services.GetRequiredService<IServer>(), "notes", TestContext.Current.CancellationToken)).Port;
            using var http = new HttpClient();

            Assert.Equal("scope notes", await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/api/secret"), TestContext.Current.CancellationToken));
            using var rebound = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://127.0.0.1:{port}/api/secret"));
            rebound.Headers.Host = "rebound.example";
            using var response = await http.SendAsync(rebound, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(directory, recursive: true);
        }
    }
}
