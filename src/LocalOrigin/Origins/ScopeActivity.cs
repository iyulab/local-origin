namespace LocalOrigin.Origins;

/// <summary>
/// What each scope's origin is serving right now: how many of its requests are in flight, and when that last changed.
/// The origin sees every request its pages make to it — and under the default security profile a page reaches no other
/// host — so «nothing pending since T» is a signal a driver can combine with what it sees in the document, instead of
/// waiting a fixed time for a page to settle.
/// </summary>
/// <remarks>
/// A request that stays open — an event stream, a long poll — counts as pending for as long as it is open; how many such
/// requests still count as settled is the reader's call. Bounded: past <see cref="MaxScopes"/>, the scope that has been
/// idle longest is forgotten; a scope with a request in flight is never forgotten.
/// </remarks>
public sealed class ScopeActivity
{
    /// <summary>How many scopes are remembered when none is given.</summary>
    public const int DefaultMaxScopes = 256;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _scopes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <param name="time">The clock; tests substitute one.</param>
    /// <param name="maxScopes">How many scopes are remembered.</param>
    public ScopeActivity(TimeProvider? time = null, int maxScopes = DefaultMaxScopes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxScopes, 1);
        _time = time ?? TimeProvider.System;
        MaxScopes = maxScopes;
    }

    /// <summary>How many scopes are remembered.</summary>
    public int MaxScopes { get; }

    /// <summary>
    /// Counts a request to <paramref name="scope"/> as in flight until the returned handle is disposed (once; disposing
    /// again does nothing).
    /// </summary>
    public IDisposable Begin(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_lock)
        {
            if (!_scopes.TryGetValue(scope, out var entry))
            {
                ForgetIdlest();
                _scopes[scope] = entry = new Entry();
            }

            entry.Pending++;
            entry.LastChange = _time.GetUtcNow();
        }

        return new Request(this, scope);
    }

    /// <summary>
    /// What <paramref name="scope"/> is serving now, or <see langword="null"/> when no request to it has been seen (or it
    /// was forgotten).
    /// </summary>
    public ScopeActivityState? Read(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_lock)
            return _scopes.TryGetValue(scope, out var entry) ? new ScopeActivityState(entry.Pending, entry.LastChange) : null;
    }

    /// <summary>Forgets <paramref name="scope"/>, unless a request to it is in flight. Returns whether it was forgotten.</summary>
    public bool Forget(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_lock)
            return _scopes.TryGetValue(scope, out var entry) && entry.Pending == 0 && _scopes.Remove(scope);
    }

    private void End(string scope)
    {
        lock (_lock)
        {
            if (!_scopes.TryGetValue(scope, out var entry)) return;
            entry.Pending--;
            entry.LastChange = _time.GetUtcNow();
        }
    }

    private void ForgetIdlest()
    {
        while (_scopes.Count >= MaxScopes)
        {
            var idle = _scopes.Where(s => s.Value.Pending == 0).ToList();
            if (idle.Count == 0) return;
            _scopes.Remove(idle.MinBy(s => s.Value.LastChange).Key);
        }
    }

    private sealed class Entry
    {
        public int Pending;
        public DateTimeOffset LastChange;
    }

    private sealed class Request(ScopeActivity owner, string scope) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.End(scope);
        }
    }
}

/// <summary>What a scope's origin is serving at one moment.</summary>
/// <param name="Pending">How many of its requests are in flight.</param>
/// <param name="LastChange">When a request to it last started or ended.</param>
public readonly record struct ScopeActivityState(int Pending, DateTimeOffset LastChange)
{
    /// <summary>
    /// Whether no more than <paramref name="allowPending"/> requests are in flight and that has held for at least
    /// <paramref name="quiet"/> at <paramref name="now"/>.
    /// </summary>
    public bool IsSettled(TimeSpan quiet, DateTimeOffset now, int allowPending = 0) =>
        Pending <= allowPending && now - LastChange >= quiet;
}
