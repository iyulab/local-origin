using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace LocalOrigin.AspNetCore.Keys;

/// <summary>
/// Page keys: what lets a write prove it comes from a page the host opened, not from any client that can reach the port.
/// </summary>
/// <remarks>
/// <para>
/// A loopback port answers every account on the computer, and whatever a page is served — its document, its injected
/// scripts, a cookie — any client can fetch too. The request header alone (<see cref="Files.FileChannelOptions.RequestHeader"/>)
/// keeps out other origins in a browser, not other programs. A page key is the one thing that is never served: the host
/// opens a page at <see cref="Address"/>, which carries the scope's key in the address's fragment (a browser never sends a
/// fragment to the server), and <see cref="Script"/>, placed in front of the page before its own code, takes the key from
/// there, keeps it for the tab (<c>sessionStorage</c>), removes it from the address bar, and adds it in a header of its own
/// (<see cref="KeyHeaderOf"/>) to the page's same-origin requests that carry the request header with the value <c>1</c> — so a
/// page written for the plain header saves unchanged, and every check of the plain header (problem reports, a preview's
/// discard, a host's own endpoints) goes on working in a keyed tab.
/// </para>
/// <para>
/// A scope's key is derived from the host's secret (HMAC-SHA256), so it is the same after a restart as long as the host keeps
/// its secret: a page left open keeps saving. Give each scope's channels the same <see cref="PageKeys"/>
/// (<c>Keys</c> on their options); a channel without keys accepts the plain header as before.
/// </para>
/// <para>
/// What it does not cover: reads (a <c>GET</c> needs no key); a page opened at its address without the fragment (a
/// bookmark, a typed address) — it has no key, and its writes are refused; and requests the script cannot see — from a
/// <c>Worker</c>, or <c>navigator.sendBeacon</c> — which carry no key.
/// </para>
/// </remarks>
public sealed class PageKeys
{
    /// <summary>The fragment parameter that carries the key in an address the host opens.</summary>
    public const string FragmentParameter = "local-origin-key";

    /// <summary>The header that carries the key, beside <paramref name="header"/> (which keeps the value <c>1</c>).</summary>
    public static string KeyHeaderOf(string header)
    {
        ArgumentException.ThrowIfNullOrEmpty(header);
        return header + "-Key";
    }

    /// <summary>The smallest secret accepted, in bytes.</summary>
    public const int MinSecretBytes = 32;

    private static readonly byte[] Label = Encoding.UTF8.GetBytes("local-origin page key\0");

    private static readonly Lazy<string> ScriptText = new(() =>
    {
        using var stream = typeof(PageKeys).Assembly.GetManifestResourceStream("LocalOrigin.AspNetCore.page-key.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly byte[] _secret;

    /// <param name="secret">The host's secret, at least <see cref="MinSecretBytes"/> bytes. Keep it where only the person's
    /// account can read it; a new secret ends the saves of every page opened with the old one.</param>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is shorter than <see cref="MinSecretBytes"/>.</exception>
    public PageKeys(ReadOnlySpan<byte> secret)
    {
        if (secret.Length < MinSecretBytes)
            throw new ArgumentException($"A page-key secret has at least {MinSecretBytes} bytes.", nameof(secret));
        _secret = secret.ToArray();
    }

    /// <summary>Keys from a new random secret — they last as long as this object, so a restart ends open pages' saves.</summary>
    public static PageKeys Ephemeral() => new(RandomNumberGenerator.GetBytes(MinSecretBytes));

    /// <summary>A new random secret for <see cref="PageKeys(ReadOnlySpan{byte})"/>, for a host that keeps one.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(MinSecretBytes);

    /// <summary>The key of <paramref name="scope"/>: URL-safe, without padding.</summary>
    public string KeyOf(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var message = new byte[Label.Length + Encoding.UTF8.GetByteCount(scope)];
        Label.CopyTo(message, 0);
        Encoding.UTF8.GetBytes(scope, message.AsSpan(Label.Length));
        return Base64Url(HMACSHA256.HashData(_secret, message));
    }

    /// <summary>
    /// <paramref name="url"/> with <paramref name="scope"/>'s key in its fragment — the address to open the page at. A fragment
    /// the URL already has is kept; the script removes only the key. Hand this address only to what opens the page, never to
    /// text a person or a model reads — and mind what records an address on the host's side before the script removes the key
    /// (a browsing history, a session restore, an agent's view of the current address): such a record holds the key.
    /// </summary>
    public string Address(string url, string scope)
    {
        ArgumentNullException.ThrowIfNull(url);
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var key = $"{FragmentParameter}={KeyOf(scope)}";
        return hash < 0 ? $"{url}#{key}" : hash == url.Length - 1 ? url + key : $"{url}&{key}";
    }

    /// <summary>
    /// Whether <paramref name="request"/> carries <paramref name="scope"/>'s key in <paramref name="header"/>'s key header
    /// (<see cref="KeyHeaderOf"/>). The plain header itself is checked by the channel.
    /// </summary>
    public bool Holds(HttpRequest request, string scope, string header)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sent = request.Headers[KeyHeaderOf(header)];
        if (sent.Count != 1 || sent[0] is not { } value) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value), Encoding.ASCII.GetBytes(KeyOf(scope)));
    }

    /// <summary>
    /// The script to place in front of a page, before any of its own code and before the channels' scripts (no
    /// <c>&lt;script&gt;</c> element around it; ASCII). <paramref name="header"/> is the request header the page's writes carry;
    /// the script adds <see cref="KeyHeaderOf"/> beside it and leaves its value <c>1</c> as it is. A request it does not change
    /// reaches the network exactly as the page made it.
    /// </summary>
    public static string Script(string header = "X-Local-Origin")
    {
        ArgumentException.ThrowIfNullOrEmpty(header);
        if (!Ascii.IsValid(header) || header.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("A header name is ASCII letters, digits and hyphens.", nameof(header));
        return ScriptText.Value
            .Replace("__LOCAL_ORIGIN_KEY_PARAM__", FragmentParameter, StringComparison.Ordinal)
            .Replace("__LOCAL_ORIGIN_KEY_HEADER__", header, StringComparison.Ordinal);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
