namespace LocalOrigin.Previews;

/// <summary>
/// What went wrong while a page was tried out: errors its own code threw or reported, and what the
/// content security policy refused. Collected while the page is served, in the order it arrives, and readable at any
/// time — whole, or only what arrived after what a reader has already seen (<see cref="Since"/>), so a driver can read
/// findings during a long run. Bounded, and without repeats, so a page that fails in a loop cannot grow it.
/// </summary>
public sealed class PreviewReport
{
    /// <summary>How many entries, errors and refusals together, a report keeps when no bound is given.</summary>
    public const int DefaultMaxEntries = 20;

    private readonly Lock _lock = new();
    private readonly List<PreviewEntry> _entries = [];
    private readonly HashSet<string> _errorMessages = new(StringComparer.Ordinal);
    private readonly HashSet<BlockedRequest> _blocked = [];
    private readonly TimeProvider _time;
    private int _dropped;

    /// <param name="maxEntries">How many entries it keeps.</param>
    /// <param name="time">The clock that stamps entries; tests substitute one.</param>
    public PreviewReport(int maxEntries = DefaultMaxEntries, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        MaxEntries = maxEntries;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many entries, errors and refusals together, it keeps.</summary>
    public int MaxEntries { get; }

    /// <summary>Everything it holds, in the order it arrived; an entry's <see cref="PreviewEntry.Sequence"/> is its index here.</summary>
    public IReadOnlyList<PreviewEntry> Entries
    {
        get { lock (_lock) return [.. _entries]; }
    }

    /// <summary>
    /// What arrived from <paramref name="sequence"/> on — pass how many entries were already read (the last one's
    /// <see cref="PreviewEntry.Sequence"/> plus one) to read only what is new.
    /// </summary>
    public IReadOnlyList<PreviewEntry> Since(int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        lock (_lock) return sequence >= _entries.Count ? [] : [.. _entries.Skip(sequence)];
    }

    /// <summary>
    /// Errors its own code threw (lines counted as in the page's own file) or reported with <c>console.error</c>, as text.
    /// </summary>
    public IReadOnlyList<string> Errors
    {
        get { lock (_lock) return [.. _entries.OfType<PreviewError>().Select(e => e.Message)]; }
    }

    /// <summary>What the content security policy refused.</summary>
    public IReadOnlyList<BlockedRequest> Blocked
    {
        get { lock (_lock) return [.. _entries.OfType<PreviewRefusal>().Select(e => e.Blocked)]; }
    }

    /// <summary>Whether nothing went wrong.</summary>
    public bool IsEmpty
    {
        get { lock (_lock) return _entries.Count == 0 && _dropped == 0; }
    }

    /// <summary>How many new problems arrived after it was full and were not kept.</summary>
    public int Dropped
    {
        get { lock (_lock) return _dropped; }
    }

    /// <summary>Adds an error, unless one with the same message is already there or the report is full.</summary>
    /// <param name="message">What the page's code said.</param>
    /// <param name="whileLoading">Whether it happened while the page was loading — it then stopped the page from starting —
    /// or after; <see langword="null"/> when not known.</param>
    /// <param name="source">Where it came from, when known.</param>
    /// <param name="position">Where in the code, when known.</param>
    public void AddError(string message, bool? whileLoading = null, ErrorSource? source = null, SourcePosition? position = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            if (_errorMessages.Contains(message)) return;
            if (Full()) return;
            _errorMessages.Add(message);
            _entries.Add(new PreviewError(_entries.Count, _time.GetUtcNow(), message, whileLoading, source, position));
        }
    }

    /// <summary>Adds a refusal, unless it is already there or the report is full.</summary>
    public void AddBlocked(BlockedRequest blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        lock (_lock)
        {
            if (_blocked.Contains(blocked)) return;
            if (Full()) return;
            _blocked.Add(blocked);
            _entries.Add(new PreviewRefusal(_entries.Count, _time.GetUtcNow(), blocked));
        }
    }

    private bool Full()
    {
        if (_entries.Count < MaxEntries) return false;
        _dropped++;
        return true;
    }
}

/// <summary>One thing a <see cref="PreviewReport"/> holds.</summary>
/// <param name="Sequence">Its place in arrival order, from 0 — its index in <see cref="PreviewReport.Entries"/>.</param>
/// <param name="At">When the report took it in — to set beside what else a driver saw at that time (a page settling, say).</param>
public abstract record PreviewEntry(int Sequence, DateTimeOffset At);

/// <summary>An error the page's own code threw or reported.</summary>
/// <param name="Sequence">Its place in arrival order.</param>
/// <param name="At">When the report took it in.</param>
/// <param name="Message">What the code said; a thrown error's line is counted as in the page's own file.</param>
/// <param name="WhileLoading">Whether it happened while the page was loading, or after; <see langword="null"/> when not known.</param>
/// <param name="Source">Where it came from, when known.</param>
/// <param name="Position">Where in the code, when known.</param>
public sealed record PreviewError(int Sequence, DateTimeOffset At, string Message, bool? WhileLoading = null,
    ErrorSource? Source = null, SourcePosition? Position = null) : PreviewEntry(Sequence, At);

/// <summary>Something the content security policy refused.</summary>
/// <param name="Sequence">Its place in arrival order.</param>
/// <param name="At">When the report took it in.</param>
/// <param name="Blocked">What was refused.</param>
public sealed record PreviewRefusal(int Sequence, DateTimeOffset At, BlockedRequest Blocked) : PreviewEntry(Sequence, At);

/// <summary>Where a page's error came from.</summary>
public enum ErrorSource
{
    /// <summary>An error its code threw and nothing caught.</summary>
    Uncaught,

    /// <summary>A promise its code rejected and nothing handled.</summary>
    Rejection,

    /// <summary>An error the page reported itself with <c>console.error</c>.</summary>
    Console,

    /// <summary>A script, style sheet or other file from another host that could not be loaded.</summary>
    Resource,
}

/// <summary>Where in a page's code an error was thrown.</summary>
/// <param name="Line">The line, counted as in that file (1-based) — for the page's own file, without the markup placed in front of it.</param>
/// <param name="Column">The column (1-based), when the browser gave it.</param>
/// <param name="File">The path of another file of the page's origin (<c>/app.js</c>), or <see langword="null"/> for the page's own file.</param>
public sealed record SourcePosition(int Line, int? Column = null, string? File = null);

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
