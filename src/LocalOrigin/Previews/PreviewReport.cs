namespace LocalOrigin.Previews;

/// <summary>
/// What went wrong while a page was tried out: errors its own code threw while loading, and what the
/// content security policy refused. Collected while the page is served; read by the host afterwards.
/// Bounded, and without repeats, so a page that fails in a loop cannot grow it.
/// </summary>
public sealed class PreviewReport
{
    /// <summary>How many entries, errors and refusals together, a report keeps.</summary>
    public const int MaxEntries = 20;

    private readonly Lock _lock = new();
    private readonly List<string> _errors = [];
    private readonly List<BlockedRequest> _blocked = [];

    /// <summary>Errors thrown while the page loaded, with lines counted as in the page's own file.</summary>
    public IReadOnlyList<string> Errors
    {
        get { lock (_lock) return [.. _errors]; }
    }

    /// <summary>What the content security policy refused.</summary>
    public IReadOnlyList<BlockedRequest> Blocked
    {
        get { lock (_lock) return [.. _blocked]; }
    }

    /// <summary>Whether nothing went wrong.</summary>
    public bool IsEmpty
    {
        get { lock (_lock) return _errors.Count == 0 && _blocked.Count == 0; }
    }

    /// <summary>Adds an error, unless it is already there or the report is full.</summary>
    public void AddError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
            if (_errors.Count + _blocked.Count < MaxEntries && !_errors.Contains(message)) _errors.Add(message);
    }

    /// <summary>Adds a refusal, unless it is already there or the report is full.</summary>
    public void AddBlocked(BlockedRequest blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        lock (_lock)
            if (_errors.Count + _blocked.Count < MaxEntries && !_blocked.Contains(blocked)) _blocked.Add(blocked);
    }
}

/// <summary>Something the content security policy refused, in terms a person can be told.</summary>
/// <param name="Category">What it was: see <see cref="BlockedCategory"/>.</param>
/// <param name="Host">The host it would have come from or gone to.</param>
public sealed record BlockedRequest(BlockedCategory Category, string Host);

/// <summary>What kind of thing the content security policy refused.</summary>
public enum BlockedCategory
{
    /// <summary>Code the page needs: a script, style sheet, font, worker or manifest from another host.</summary>
    Library,

    /// <summary>Anything fetched or displayed from another host.</summary>
    Data,

    /// <summary>A form submitted to another host.</summary>
    Form,
}
