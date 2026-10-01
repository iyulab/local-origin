namespace LocalOrigin.Origins;

/// <summary>
/// Where the port each listener was last served on is remembered. An origin includes its port, and
/// everything a browser keeps per origin is lost when it changes, so a port is remembered for as long
/// as it can be used. <see cref="PortMemoryFile"/> keeps them in one file; a host that already
/// remembers ports somewhere else can implement this over that record instead.
/// </summary>
public interface IPortMemory
{
    /// <summary>The port remembered for <paramref name="key"/>, or <see langword="null"/>.</summary>
    int? Recall(string key);

    /// <summary>Remembers <paramref name="port"/> for <paramref name="key"/>. Complete when it returns.</summary>
    void Remember(string key, int port);
}
