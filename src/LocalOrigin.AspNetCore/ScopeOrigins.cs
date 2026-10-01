using LocalOrigin.Origins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LocalOrigin.AspNetCore;

/// <summary>
/// Separates a host's traffic from its scopes' at the front of the pipeline. A request that names a scope is answered by
/// the host's scope handler and nothing else — it never reaches the host's own routes, so no route of the host is reachable
/// from a page's origin by construction rather than by checking each route. Every such response carries the security
/// profile, and a request another site makes is refused before the handler runs.
/// </summary>
public static class ScopeOrigins
{
    /// <summary>
    /// Answers every request that names a scope (<see cref="IOriginStrategy.ScopeOf"/>) with <paramref name="handle"/>;
    /// everything else continues down the pipeline. A request arriving on a port the strategy owns
    /// (<see cref="IOriginStrategy.OwnsPort"/>) that names no scope is refused with <c>421 Misdirected Request</c> — a public
    /// name rebound to the loopback address must reach neither a scope nor the host.
    /// </summary>
    /// <param name="app">The host's pipeline; call this before routing.</param>
    /// <param name="strategy">How scopes get origins.</param>
    /// <param name="handle">Answers a request addressed to the given scope — static files, the write channels, whatever the
    /// host serves there. The profile's headers are already on the response; a handler may override them for its own
    /// responses (<see cref="Files.ScopeFiles"/> does, for files that do not render as pages).</param>
    /// <param name="profile">The headers every scope response carries and which requests are refused; the default when null.</param>
    public static IApplicationBuilder UseScopeOrigins(this IApplicationBuilder app, IOriginStrategy strategy,
        Func<HttpContext, string, Task> handle, OriginSecurityProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(handle);
        profile ??= new OriginSecurityProfile();

        return app.Use(async (context, next) =>
        {
            var scope = strategy.ScopeOf(context.Request);
            if (scope is null)
            {
                if (strategy.OwnsPort(context.Connection.LocalPort))
                {
                    profile.Apply(context.Response);
                    context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    return;
                }

                await next(context).ConfigureAwait(false);
                return;
            }

            profile.Apply(context.Response);
            if (profile.Refuses(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await handle(context, scope).ConfigureAwait(false);
        });
    }
}
