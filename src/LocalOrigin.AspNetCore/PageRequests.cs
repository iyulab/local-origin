using Microsoft.AspNetCore.Http;

namespace LocalOrigin.AspNetCore;

/// <summary>Checks shared by every request a page's injected script makes back to the host.</summary>
internal static class PageRequests
{
    /// <summary>
    /// Whether the request carries <paramref name="header"/> (with the value <c>1</c>), which only the page's own
    /// script sends, and — when the browser names an origin — that origin is the one it was sent to. A page on
    /// another origin cannot add the header without a CORS preflight, which is never granted.
    /// </summary>
    public static bool IsFromThePage(HttpRequest request, string header) =>
        request.Headers[header] == "1" && SameOriginOrAbsent(request);

    /// <summary>
    /// As <see cref="IsFromThePage(HttpRequest, string)"/>, and with <paramref name="keys"/> the header must hold
    /// <paramref name="scope"/>'s page key instead of <c>1</c> (<see cref="Keys.PageKeys"/>) — a client that can reach the port
    /// but was not handed the page's address cannot write.
    /// </summary>
    public static bool IsFromThePage(HttpRequest request, string header, Keys.PageKeys? keys, string scope) =>
        keys is null ? IsFromThePage(request, header) : keys.Holds(request, scope, header) && SameOriginOrAbsent(request);

    /// <summary>Whether the browser names no origin, or the one the request was sent to.</summary>
    public static bool SameOriginOrAbsent(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
