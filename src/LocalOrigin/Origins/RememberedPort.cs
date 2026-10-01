namespace LocalOrigin.Origins;

/// <summary>
/// Starts a listener on the port remembered for it, so its origins keep their address. Only when the
/// port stays taken does it start on a new one — and says so, because every origin on it moved and the
/// person may need to be told.
/// </summary>
public static class RememberedPort
{
    /// <summary>
    /// Starts a listener for <paramref name="key"/>: on the remembered port when there is one — retrying for a
    /// moment, since its likeliest holder is the host's own previous run, still stopping — and otherwise on a
    /// port the operating system chooses. The port it ends up on is remembered.
    /// </summary>
    /// <typeparam name="T">The started listener.</typeparam>
    /// <param name="memory">Where ports are remembered.</param>
    /// <param name="key">What the port is remembered under.</param>
    /// <param name="start">Starts a listener on the given port (0: any) and returns it. Throws <see cref="IOException"/> when the port is taken, having released whatever it built for the attempt.</param>
    /// <param name="portOf">The port a started listener is listening on.</param>
    /// <param name="options">How long to wait for the remembered port.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    public static async Task<RememberedPortResult<T>> StartAsync<T>(IPortMemory memory, string key, Func<int, Task<T>> start, Func<T, int> portOf,
        RememberedPortOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(portOf);
        options ??= new RememberedPortOptions();

        var remembered = memory.Recall(key);
        if (remembered is { } port)
        {
            for (var attempt = 0; attempt < options.Attempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(options.RetryDelay, cancellationToken).ConfigureAwait(false);
                try
                {
                    return new(await start(port).ConfigureAwait(false), port, null);
                }
                catch (IOException)
                {
                    // Still taken.
                }
            }
        }

        var started = await start(0).ConfigureAwait(false);
        var chosen = portOf(started);
        memory.Remember(key, chosen);
        return new(started, chosen, remembered);
    }
}

/// <summary>How long <see cref="RememberedPort.StartAsync"/> waits for a remembered port.</summary>
public sealed record RememberedPortOptions
{
    /// <summary>How many times the remembered port is tried before a new one is taken.</summary>
    public int Attempts { get; init; } = 5;

    /// <summary>The wait between tries.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>A listener started by <see cref="RememberedPort.StartAsync"/>.</summary>
/// <typeparam name="T">The started listener.</typeparam>
/// <param name="Listener">The started listener.</param>
/// <param name="Port">The port it listens on.</param>
/// <param name="PreviousPort">
/// The port remembered before, when it could not be used again — every origin on it moved.
/// <see langword="null"/> when nothing moved (including a first start).
/// </param>
public sealed record RememberedPortResult<T>(T Listener, int Port, int? PreviousPort);
