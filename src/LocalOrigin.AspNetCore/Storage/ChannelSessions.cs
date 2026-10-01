using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LocalOrigin.AspNetCore.Storage;

/// <summary>
/// Who may write to which scope. Serving a scope's document issues a session token (sent as an HTTP-only
/// cookie of that scope's origin) and a tab identifier (given to the page's script). A write must present
/// both, and both must belong to the scope whose origin received the request.
/// </summary>
public sealed class ChannelSessions
{
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChannelTab> _tabs = new(StringComparer.Ordinal);
    // Tabs ended by Revoke, kept so a write that arrives from one afterwards is recognised as the
    // scope's own page (refused, and reported) rather than a stranger.
    private readonly ConcurrentDictionary<string, ChannelTab> _retired = new(StringComparer.Ordinal);

    /// <summary>Issues a session of <paramref name="scope"/>.</summary>
    public string IssueSession(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var token = NewToken();
        _sessions[token] = scope;
        return token;
    }

    /// <summary>Issues a tab of <paramref name="scope"/>: one loaded page.</summary>
    public string IssueTab(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var id = NewToken();
        _tabs[id] = new ChannelTab(scope);
        return id;
    }

    /// <summary>Whether <paramref name="sessionToken"/> is a session of <paramref name="scope"/>.</summary>
    public bool IsSession(string scope, string? sessionToken) =>
        sessionToken is not null && _sessions.TryGetValue(sessionToken, out var sessionScope) && sessionScope == scope;

    /// <summary>The tab, if both it and the session belong to <paramref name="scope"/>.</summary>
    public ChannelTab? Authorize(string scope, string? sessionToken, string? tabId)
    {
        if (tabId is null || !IsSession(scope, sessionToken)) return null;
        return _tabs.TryGetValue(tabId, out var tab) && tab.Scope == scope ? tab : null;
    }

    /// <summary>The tab, if it exists and belongs to <paramref name="scope"/> — for the host, which holds no session.</summary>
    public ChannelTab? Find(string scope, string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        return _tabs.TryGetValue(tabId, out var tab) && tab.Scope == scope ? tab : null;
    }

    /// <summary>A tab of <paramref name="scope"/> that <see cref="Revoke"/> ended, if <paramref name="tabId"/> names one.</summary>
    public ChannelTab? Retired(string scope, string? tabId) =>
        tabId is not null && _retired.TryGetValue(tabId, out var tab) && tab.Scope == scope ? tab : null;

    /// <summary>
    /// Ends every session and tab of <paramref name="scope"/>. A page loaded before this can no longer write —
    /// for when the scope's code is replaced, so a page still running the old code cannot write into data the
    /// new code now owns.
    /// </summary>
    public void Revoke(string scope)
    {
        foreach (var (token, owner) in _sessions)
            if (owner == scope) _sessions.TryRemove(token, out _);
        foreach (var (id, tab) in _tabs)
            if (tab.Scope == scope && _tabs.TryRemove(id, out _)) _retired[id] = tab;
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// One loaded page. Tracks the highest operation sequence applied from it, and the highest sequence the
/// page said it had issued — more than applied means a write has not arrived.
/// </summary>
public sealed class ChannelTab
{
    internal ChannelTab(string scope)
    {
        Scope = scope;
    }

    /// <summary>The scope the page belongs to.</summary>
    public string Scope { get; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The highest sequence applied from the page.</summary>
    public long LastSequence { get; internal set; }

    /// <summary>The highest sequence the page said it had issued.</summary>
    public long Issued { get; internal set; }

    /// <summary>Whether the page's report sent after it left has arrived — then <see cref="Issued"/> is final.</summary>
    public bool Left { get; internal set; }
}
