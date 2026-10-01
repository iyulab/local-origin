namespace LocalOrigin.Origins;

/// <summary>
/// How scopes get origins, and which scope a request is addressed to. The browser isolates at
/// scheme + host + port, so a strategy varies the host (<see cref="SubdomainOrigins"/>) or the port
/// (<see cref="PortOrigins"/>); the host chooses.
/// </summary>
public interface IOriginStrategy
{
    /// <summary>The origin of <paramref name="scope"/>, ending in <c>/</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is not a valid name.</exception>
    /// <exception cref="InvalidOperationException">The strategy has no origin for <paramref name="scope"/> yet.</exception>
    Uri OriginOf(string scope);

    /// <summary>
    /// The scope a request is addressed to, from the host name it names (the <c>Host</c> header, without
    /// its port) and the local port it arrived on, or <see langword="null"/> when it names none. A request
    /// naming any other host — including a public name rebound to the loopback address — names no scope.
    /// </summary>
    string? ScopeOf(string hostName, int localPort);
}
