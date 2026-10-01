using System.Net;
using System.Net.Sockets;

namespace LocalOrigin.Origins;

/// <summary>
/// A port per scope on one address: <c>http://127.0.0.1:&lt;port&gt;/</c>. For hosts that must be reachable
/// from any client or resolver. Each scope is bound to the port its listener ended up on
/// (<see cref="Bind"/>); the host opens one listener per scope, usually through <see cref="RememberedPort"/>
/// so a scope keeps its port — and with it its origin and stored data — across restarts.
/// </summary>
public sealed class PortOrigins : IOriginStrategy
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _ports = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _scopes = [];

    /// <param name="address">The address every scope is served on. Loopback unless the host deliberately opens it.</param>
    public PortOrigins(IPAddress? address = null)
    {
        Address = address ?? IPAddress.Loopback;
    }

    /// <summary>The address every scope is served on.</summary>
    public IPAddress Address { get; }

    /// <summary>The scopes with a port, and their ports.</summary>
    public IReadOnlyDictionary<string, int> Bound
    {
        get { lock (_lock) return new Dictionary<string, int>(_ports, StringComparer.Ordinal); }
    }

    /// <summary>Gives <paramref name="scope"/> the origin on <paramref name="port"/>, replacing any port it had.</summary>
    /// <exception cref="InvalidOperationException">Another scope has <paramref name="port"/>.</exception>
    public void Bind(string scope, int port)
    {
        if (!ScopeName.IsValid(scope)) throw new ArgumentException($"'{scope}' is not a valid scope name.", nameof(scope));
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        lock (_lock)
        {
            if (_scopes.TryGetValue(port, out var holder) && holder != scope)
                throw new InvalidOperationException($"Port {port} already belongs to scope '{holder}'.");
            if (_ports.TryGetValue(scope, out var previous)) _scopes.Remove(previous);
            _ports[scope] = port;
            _scopes[port] = scope;
        }
    }

    /// <summary>Takes <paramref name="scope"/>'s origin away. Returns whether it had one.</summary>
    public bool Unbind(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_lock)
        {
            if (!_ports.Remove(scope, out var port)) return false;
            _scopes.Remove(port);
            return true;
        }
    }

    /// <inheritdoc />
    public Uri OriginOf(string scope)
    {
        if (!ScopeName.IsValid(scope)) throw new ArgumentException($"'{scope}' is not a valid scope name.", nameof(scope));
        int port;
        lock (_lock)
            if (!_ports.TryGetValue(scope, out port)) throw new InvalidOperationException($"Scope '{scope}' has no port yet.");
        var host = Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString();
        return new Uri($"http://{host}:{port}/");
    }

    /// <inheritdoc />
    /// <remarks>
    /// The scope is the one bound to the port the request arrived on. The host name must be the address
    /// itself — or <c>localhost</c> when the address is a loopback one — so a public name rebound to the
    /// address names no scope.
    /// </remarks>
    public string? ScopeOf(string hostName, int localPort)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        var named = IPAddress.TryParse(hostName.Trim('[', ']'), out var parsed)
            ? parsed.Equals(Address)
            : IPAddress.IsLoopback(Address) && hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!named) return null;
        lock (_lock) return _scopes.GetValueOrDefault(localPort);
    }

    /// <inheritdoc />
    /// <remarks>A port is owned while a scope is bound to it.</remarks>
    public bool OwnsPort(int localPort)
    {
        lock (_lock) return _scopes.ContainsKey(localPort);
    }
}
