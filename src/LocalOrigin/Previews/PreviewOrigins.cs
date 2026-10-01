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
            // The oldest goes first; a caller that never removes its previews cannot grow this.
            while (_previews.Count >= Options.MaxPreviews)
                _previews.Remove(_previews.MinBy(p => p.Value.Created).Key);
            _previews[name] = preview;
        }

        return preview;
    }

    /// <summary>The preview whose scope is <paramref name="scope"/>, while it lives.</summary>
    public Preview<T>? Find(string? scope)
    {
        if (scope is null) return null;
        lock (_lock)
        {
            Sweep();
            return _previews.GetValueOrDefault(scope);
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
        foreach (var expired in _previews.Where(p => now - p.Value.Created > Options.Lifetime).Select(p => p.Key).ToList())
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
    }

    /// <summary>The preview's scope name: its origin, under the host's strategy.</summary>
    public string Scope { get; }

    /// <summary>What the host keeps with it.</summary>
    public T Content { get; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; }

    /// <summary>What went wrong while it was served.</summary>
    public PreviewReport Report { get; } = new();
}

/// <summary>Naming, lifetime and bounds of <see cref="PreviewOrigins{T}"/>.</summary>
public sealed record PreviewOptions
{
    /// <summary>What every preview's scope name starts with, followed by 32 random hexadecimal digits.</summary>
    public string NamePrefix { get; init; } = "pv-";

    /// <summary>How long a preview stays servable after it was made.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How many previews are held at once.</summary>
    public int MaxPreviews { get; init; } = 8;
}
