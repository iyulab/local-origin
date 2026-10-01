using LocalOrigin.Origins;
using Microsoft.AspNetCore.Http;

namespace LocalOrigin.AspNetCore;

/// <summary>Reading <see cref="IOriginStrategy"/> answers off an ASP.NET Core request.</summary>
public static class OriginRequests
{
    /// <summary>The scope <paramref name="request"/> is addressed to, or <see langword="null"/> (see <see cref="IOriginStrategy.ScopeOf"/>).</summary>
    public static string? ScopeOf(this IOriginStrategy strategy, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(request);
        return strategy.ScopeOf(request.Host.Host, request.HttpContext.Connection.LocalPort);
    }
}
