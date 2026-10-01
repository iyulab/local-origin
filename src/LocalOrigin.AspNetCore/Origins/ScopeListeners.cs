using System.Net;
using System.Net.Sockets;
using LocalOrigin.Origins;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;

namespace LocalOrigin.AspNetCore.Origins;

/// <summary>
/// A listener per scope for <see cref="PortOrigins"/>, opened and closed while the server runs. One server serves
/// every scope: each listener is an endpoint in the <c>Kestrel:Endpoints</c> configuration this source supplies, so
/// opening or closing one leaves the others serving, and the scope of a request is the one bound to the port it
/// arrived on (<see cref="OriginRequests.ScopeOf"/>).
/// </summary>
/// <remarks>
/// <para>
/// Add the source to the host's configuration before the server starts — <c>builder.Configuration.Sources.Add(listeners)</c>
/// — and keep Kestrel reading its <c>Kestrel</c> section with reloading on (the ASP.NET Core default). Endpoints of the
/// host's own (its API, say) can sit in the same section; endpoint names this source writes start with
/// <see cref="ScopeListenerOptions.EndpointPrefix"/>.
/// </para>
/// <para>
/// A listener is open only once the server reports its address; a port that could not be bound never shows up there,
/// so it counts as taken. A scope keeps its port across restarts through <see cref="IPortMemory"/> and
/// <see cref="RememberedPort"/>: the remembered port is tried for a moment, and only when it stays taken does the scope
/// move — the result says so, because every origin that moved lost what the browser kept for it. While a remembered
/// port is still held (often by the host's own previous run, still stopping), the server logs each failed bind as an
/// error with its stack trace; that is the wait working, not a fault.
/// </para>
/// <para>
/// A listener answers every request that reaches its port; keeping the host's own routes off scope ports (and scope
/// routes off the host's ports) is the host's pipeline's job — <see cref="OriginRequests.ScopeOf"/> answers
/// <see langword="null"/> for a request on a port no scope holds.
/// </para>
/// </remarks>
public sealed class ScopeListeners : IConfigurationSource, IDisposable
{
    private readonly Provider _provider = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="origins">The scopes' origins; a scope is bound to its port once its listener is open.</param>
    /// <param name="memory">Where each scope's port is remembered, under the scope's name.</param>
    /// <param name="options">Naming, the ports to choose from and how long to wait.</param>
    public ScopeListeners(PortOrigins origins, IPortMemory memory, ScopeListenerOptions? options = null)
    {
        Origins = origins ?? throw new ArgumentNullException(nameof(origins));
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Options = options ?? new ScopeListenerOptions();
        if (Options.Ports is { } ports && (ports.First < 1 || ports.Last > 65535 || ports.First > ports.Last))
            throw new ArgumentException("The port range is not a range of ports.", nameof(options));
    }

    /// <summary>The scopes' origins.</summary>
    public PortOrigins Origins { get; }

    /// <summary>Where each scope's port is remembered.</summary>
    public IPortMemory Memory { get; }

    /// <summary>Naming, the ports to choose from and how long to wait.</summary>
    public ScopeListenerOptions Options { get; }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;

    /// <summary>
    /// Opens <paramref name="scope"/>'s listener on <paramref name="server"/> — on its remembered port when it can — and
    /// binds the scope to that port. Opening a scope that is already open returns its port.
    /// </summary>
    /// <param name="server">The running server whose configuration includes this source.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The port, and the port the scope had before when it had to move.</returns>
    /// <exception cref="IOException">No port could be bound.</exception>
    public async Task<ScopeListener> OpenAsync(IServer server, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!ScopeName.IsValid(scope)) throw new ArgumentException($"'{scope}' is not a valid scope name.", nameof(scope));
        var addresses = AddressesOf(server);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Origins.Bound.TryGetValue(scope, out var open)) return new ScopeListener(scope, open, null);

            var started = await RememberedPort.StartAsync(Memory, scope,
                port => port == 0 ? ListenOnAnyAsync(addresses, scope, cancellationToken) : ListenAsync(addresses, scope, port, cancellationToken),
                port => port, Options.Remembered, cancellationToken).ConfigureAwait(false);
            Origins.Bind(scope, started.Port);
            return new ScopeListener(scope, started.Port, started.PreviousPort);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes <paramref name="scope"/>'s listener on <paramref name="server"/> and takes its origin away; complete once
    /// the server no longer reports the listener. Its port stays remembered, so opening it again brings back the same
    /// origin. Returns whether it was open.
    /// </summary>
    /// <exception cref="IOException">The server still reports the listener after <see cref="ScopeListenerOptions.BindTimeout"/>.</exception>
    public async Task<bool> CloseAsync(IServer server, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(scope);
        var addresses = AddressesOf(server);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Origins.Bound.TryGetValue(scope, out var port)) return false;
            Origins.Unbind(scope);
            _provider.Remove(KeyOf(scope));
            if (!await WaitForAsync(addresses, UrlOf(port), reported: false, cancellationToken).ConfigureAwait(false))
                throw new IOException($"The listener on port {port} did not close.");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<int> ListenOnAnyAsync(IServerAddressesFeature addresses, string scope, CancellationToken cancellationToken)
    {
        IOException? last = null;
        foreach (var port in Candidates())
        {
            try
            {
                return await ListenAsync(addresses, scope, port, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                last = e;
            }
        }

        throw new IOException("No free port was found for the scope.", last);
    }

    private IEnumerable<int> Candidates()
    {
        if (Options.Ports is { } range)
        {
            var bound = Origins.Bound.Values.ToHashSet();
            for (var port = range.First; port <= range.Last; port++)
                if (!bound.Contains(port) && IsFree(port)) yield return port;
            yield break;
        }

        // The operating system's choice, a few times over: the port is free when asked and can be taken before
        // the server binds it, in which case the next one is tried.
        for (var attempt = 0; attempt < 5; attempt++)
            yield return AnyFreePort();
    }

    /// <summary>A port the operating system chooses, released before it is returned so the server can bind it.</summary>
    private int AnyFreePort()
    {
        using var probe = new Socket(Origins.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(Origins.Address, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private bool IsFree(int port)
    {
        using var probe = new Socket(Origins.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        // Without it, Windows lets a second socket bind a port another holds with address reuse.
        if (OperatingSystem.IsWindows()) probe.ExclusiveAddressUse = true;
        try
        {
            probe.Bind(new IPEndPoint(Origins.Address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Adds the endpoint and waits for the server to report it; on failure removes it and throws <see cref="IOException"/>.</summary>
    private async Task<int> ListenAsync(IServerAddressesFeature addresses, string scope, int port, CancellationToken cancellationToken)
    {
        var url = UrlOf(port);
        _provider.Set(KeyOf(scope), url);
        if (await WaitForAsync(addresses, url, reported: true, cancellationToken).ConfigureAwait(false)) return port;
        _provider.Remove(KeyOf(scope));
        throw new IOException($"Port {port} could not be bound.");
    }

    /// <summary>Waits up to <see cref="ScopeListenerOptions.BindTimeout"/> for the server to report — or stop reporting — <paramref name="url"/>.</summary>
    private async Task<bool> WaitForAsync(IServerAddressesFeature addresses, string url, bool reported, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Options.BindTimeout;
        while (addresses.Addresses.Any(a => string.Equals(a.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)) != reported)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static IServerAddressesFeature AddressesOf(IServer server) =>
        server.Features.Get<IServerAddressesFeature>() ?? throw new InvalidOperationException("The server does not report its addresses.");

    private string UrlOf(int port) =>
        Origins.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{Origins.Address}]:{port}" : $"http://{Origins.Address}:{port}";

    private string KeyOf(string scope) => $"Kestrel:Endpoints:{Options.EndpointPrefix}{scope}:Url";

    private sealed class Provider : ConfigurationProvider
    {
        public new void Set(string key, string value)
        {
            Data[key] = value;
            OnReload();
        }

        public void Remove(string key)
        {
            if (Data.Remove(key)) OnReload();
        }
    }
}

/// <summary>An open listener of <see cref="ScopeListeners"/>.</summary>
/// <param name="Scope">The scope.</param>
/// <param name="Port">The port it listens on.</param>
/// <param name="PreviousPort">
/// The port the scope had before, when it could not be used again — its origin moved, and what the browser kept for
/// the old one is out of reach. <see langword="null"/> when nothing moved (including a first open).
/// </param>
public sealed record ScopeListener(string Scope, int Port, int? PreviousPort);

/// <summary>Naming, the ports to choose from and how long to wait, for <see cref="ScopeListeners"/>.</summary>
public sealed record ScopeListenerOptions
{
    /// <summary>What every endpoint name this source writes under <c>Kestrel:Endpoints</c> starts with, followed by the scope.</summary>
    public string EndpointPrefix { get; init; } = "local-origin-";

    /// <summary>
    /// The ports a scope with no usable remembered port is given: the first free one, in order. <see langword="null"/>
    /// (the default) lets the operating system choose.
    /// </summary>
    public (int First, int Last)? Ports { get; init; }

    /// <summary>How long to wait for the server to report a new listener before its port counts as taken.</summary>
    public TimeSpan BindTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a remembered port is waited for.</summary>
    public RememberedPortOptions? Remembered { get; init; }
}
