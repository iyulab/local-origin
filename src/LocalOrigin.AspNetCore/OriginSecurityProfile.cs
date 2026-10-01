using Microsoft.AspNetCore.Http;

namespace LocalOrigin.AspNetCore;

/// <summary>
/// The headers every response from a scope's origin carries, and which requests the origin refuses.
/// Decided once per host, so no response can leave one out.
/// </summary>
public sealed record OriginSecurityProfile
{
    /// <summary>
    /// Everything loads from the page's own origin; inline script and style are allowed because local
    /// pages are often single files that rely on them. Nothing — no fetch, image, script, style sheet or
    /// form submission — may reach another origin. Nor may another origin embed the page: a page
    /// elsewhere (any browser on this computer can reach the loopback address) could otherwise frame
    /// it, to trick clicks inside it.
    /// </summary>
    public const string DefaultContentSecurityPolicy =
        "default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; form-action 'self'; frame-ancestors 'self'";

    /// <summary>The <c>Content-Security-Policy</c> of every response. The host chooses it; see <see cref="DefaultContentSecurityPolicy"/>.</summary>
    public string ContentSecurityPolicy { get; init; } = DefaultContentSecurityPolicy;

    /// <summary>
    /// Whether a request another site's page makes — a script, an image, a <c>fetch</c> — is refused. The
    /// browser says where a request comes from in <c>Sec-Fetch-Site</c>; anything but the origin itself (or
    /// the person, typing an address) is refused, except navigating to the page. Sibling origins are
    /// "same-site" to the browser (other subdomains of <c>localhost</c>, other ports of one address), so
    /// they are refused too. On by default.
    /// </summary>
    public bool RefuseCrossSiteRequests { get; init; } = true;

    /// <summary>
    /// Headers every response carries, next to the content security policy.
    /// <list type="bullet">
    /// <item><c>Referrer-Policy: no-referrer</c> — following a link out would otherwise tell the other
    /// site the page's local address.</item>
    /// <item><c>Cross-Origin-Resource-Policy: same-origin</c> — a page elsewhere could otherwise pull a
    /// response in as an image or script it cannot read but can still make the browser load.</item>
    /// <item><c>X-Content-Type-Options: nosniff</c>.</item>
    /// </list>
    /// No <c>Cross-Origin-Opener-Policy</c>: with <c>same-origin</c>, leaving the page for another document
    /// swaps its browsing context group, and the writes a page sends as it closes (in <c>pagehide</c>) are
    /// lost in embedded browsers. Keeping those writes comes first.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> IsolationHeaders { get; } =
    [
        new("Referrer-Policy", "no-referrer"),
        new("Cross-Origin-Resource-Policy", "same-origin"),
        new("X-Content-Type-Options", "nosniff"),
    ];

    /// <summary>Sets the profile's headers on <paramref name="response"/>.</summary>
    public void Apply(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        foreach (var (name, value) in IsolationHeaders) response.Headers[name] = value;
    }

    /// <summary>Whether <paramref name="request"/> is one the origin refuses (see <see cref="RefuseCrossSiteRequests"/>).</summary>
    public bool Refuses(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RefuseCrossSiteRequests) return false;
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length == 0 || site is "same-origin" or "none") return false;
        return request.Headers["Sec-Fetch-Mode"] != "navigate";
    }
}
