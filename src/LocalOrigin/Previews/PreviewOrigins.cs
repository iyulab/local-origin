using System.Security.Cryptography;

namespace LocalOrigin.Previews;

/// <summary>
/// Candidate versions of pages held for a look before they replace the version people use. Each preview
/// gets a throwaway scope name — and so, under the host's origin strategy, an origin of its own, never the
/// real page's — and a <see cref="PreviewReport"/>. Held in memory only, for a short while, and bounded.
/// </summary>
/// <typeparam name="T">What the host keeps with each preview: the candidate document, what it previews, and so on.</typeparam>
/// <remarks>
/// local-origin supplies the origin and the report. How the preview is driven — a headless browser, an
/// off-screen view — and what to decide from the report are the host's.
/// </remarks>
public sealed class PreviewOrigins<T>
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Preview<T>> _previews = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <param name="options">Naming, lifetime and bounds.</param>
    /// <param name="time">The clock; tests substitute one.</param>
    public PreviewOrigins(PreviewOptions? options = null, TimeProvider? time = null)
    {
        Options = options ?? new PreviewOptions();
        _time = time ?? TimeProvider.System;
        if (!Origins.ScopeName.IsValid(Options.NamePrefix + new string('0', TokenLength)))
            throw new ArgumentException("The name prefix does not make valid scope names.", nameof(options));
    }

    private const int TokenLength = 32;

    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("0123456789abcdef");

    /// <summary>Naming, lifetime and bounds.</summary>
    public PreviewOptions Options { get; }

    /// <summary>Holds <paramref name="content"/> as a new preview and returns it. The oldest preview goes when there are too many.</summary>
    public Preview<T> Create(T content)
    {
        var name = Options.NamePrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenLength / 2));
        var preview = new Preview<T>(name, content, _time.GetUtcNow());
        lock (_lock)
        {
            Sweep();
            // The least recently used goes first; a caller that never removes its previews cannot grow this.
            while (_previews.Count >= Options.MaxPreviews)
                _previews.Remove(_previews.MinBy(p => p.Value.LastUsed).Key);
            _previews[name] = preview;
        }

        return preview;
    }

    /// <summary>
    /// The preview whose scope is <paramref name="scope"/>, while it lives. With <see cref="PreviewOptions.RenewOnUse"/>,
    /// finding it counts as using it.
    /// </summary>
    public Preview<T>? Find(string? scope)
    {
        if (scope is null) return null;
        lock (_lock)
        {
            Sweep();
            var preview = _previews.GetValueOrDefault(scope);
            if (preview is not null && Options.RenewOnUse) preview.LastUsed = _time.GetUtcNow();
            return preview;
        }
    }

    /// <summary>Whether <paramref name="scope"/> has the shape of a preview's scope name — whether or not that preview still lives.</summary>
    public bool IsPreviewName(string? scope) =>
        scope is not null && scope.Length == Options.NamePrefix.Length + TokenLength
        && scope.StartsWith(Options.NamePrefix, StringComparison.Ordinal)
        && !scope.AsSpan(Options.NamePrefix.Length).ContainsAnyExcept(LowerHex);

    /// <summary>Lets go of the preview whose scope is <paramref name="scope"/>. Returns whether it was held.</summary>
    public bool Remove(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_lock) return _previews.Remove(scope);
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var expired in _previews.Where(p => now - p.Value.LastUsed > Options.Lifetime).Select(p => p.Key).ToList())
            _previews.Remove(expired);
    }
}

/// <summary>One preview held by <see cref="PreviewOrigins{T}"/>.</summary>
/// <typeparam name="T">What the host keeps with it.</typeparam>
public sealed class Preview<T>
{
    internal Preview(string scope, T content, DateTimeOffset created)
    {
        Scope = scope;
        Content = content;
        Created = created;
        LastUsed = created;
    }

    /// <summary>The preview's scope name: its origin, under the host's strategy.</summary>
    public string Scope { get; }

    /// <summary>What the host keeps with it.</summary>
    public T Content { get; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; }

    /// <summary>
    /// When it was last found — with <see cref="PreviewOptions.RenewOnUse"/>; otherwise when it was made. Its lifetime
    /// counts from here.
    /// </summary>
    public DateTimeOffset LastUsed { get; internal set; }

    /// <summary>What went wrong while it was served.</summary>
    public PreviewReport Report { get; } = new();
}

/// <summary>Naming, lifetime and bounds of <see cref="PreviewOrigins{T}"/>.</summary>
public sealed record PreviewOptions
{
    /// <summary>What every preview's scope name starts with, followed by 32 random hexadecimal digits.</summary>
    public string NamePrefix { get; init; } = "pv-";

    /// <summary>How long a preview stays servable after it was made — or, with <see cref="RenewOnUse"/>, after it was last found.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether finding a preview renews its lifetime. Off, a preview lives a fixed time from when it was made — enough
    /// for a load that is checked once. On, it lives as long as it is being served or read, and its lifetime runs
    /// only once it is left alone — for a preview someone tries for as long as they like.
    /// </summary>
    public bool RenewOnUse { get; init; }

    /// <summary>How many previews are held at once.</summary>
    public int MaxPreviews { get; init; } = 8;
}
