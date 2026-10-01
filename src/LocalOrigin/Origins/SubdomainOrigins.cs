namespace LocalOrigin.Origins;

/// <summary>
/// One port, a subdomain per scope: <c>http://&lt;scope&gt;.localhost:&lt;port&gt;/</c>. Cheap and numerous,
/// for hosts whose browser engine resolves <c>*.localhost</c> to the loopback address itself.
/// </summary>
/// <param name="port">The port every scope is served on.</param>
public sealed class SubdomainOrigins(int port) : IOriginStrategy
{
    private const string Suffix = ".localhost";

    /// <summary>The port every scope is served on.</summary>
    public int Port { get; } = port is > 0 and <= 65535 ? port : throw new ArgumentOutOfRangeException(nameof(port));

    /// <inheritdoc />
    public Uri OriginOf(string scope)
    {
        if (!ScopeName.IsValid(scope)) throw new ArgumentException($"'{scope}' is not a valid scope name.", nameof(scope));
        return new Uri($"http://{scope}{Suffix}:{Port}/");
    }

    /// <inheritdoc />
    public string? ScopeOf(string hostName, int localPort)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        if (localPort != Port || !hostName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var label = hostName[..^Suffix.Length].ToLowerInvariant();
        return ScopeName.IsValid(label) ? label : null;
    }
}
