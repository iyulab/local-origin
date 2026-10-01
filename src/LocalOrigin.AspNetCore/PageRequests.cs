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

    /// <summary>Whether the browser names no origin, or the one the request was sent to.</summary>
    public static bool SameOriginOrAbsent(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
