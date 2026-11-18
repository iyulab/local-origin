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

    /// <summary>
    /// Every key with a remembered port. A remembered port is reserved for its key: a listener that has to choose a port
    /// does not choose one remembered for another key, so a key that is away for a while finds its port again.
    /// </summary>
    IReadOnlyDictionary<string, int> Remembered { get; }

    /// <summary>
    /// Forgets <paramref name="key"/>'s port, freeing it for other keys. For a key that is gone for good; one that is only
    /// away keeps its port. Returns whether a port was remembered. Complete when it returns.
    /// </summary>
    bool Forget(string key);
}
