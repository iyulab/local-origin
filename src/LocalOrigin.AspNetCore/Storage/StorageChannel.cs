using System.Text.Json;
using System.Text.Json.Serialization;
using LocalOrigin.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalOrigin.AspNetCore.Storage;

/// <summary>
/// The storage channel: for pages that were never written for any host and only know <c>localStorage</c>.
/// A script placed in front of the page (<see cref="Open"/>) replaces <c>window.localStorage</c> with a
/// synchronous map seeded from the scope's <see cref="KeyValueStore"/> and sends every change back here
/// (<see cref="HandleAsync"/>), where it is journaled before it is acknowledged.
/// </summary>
/// <remarks>
/// Wire: <c>POST</c> to <see cref="StorageChannelOptions.Path"/> with
/// <c>{ "tab": "...", "ops": [ { "seq": 1, "op": "set", "key": "...", "value": "..." }, ... ], "issued": n }</c>
/// (<c>issued</c>: the last sequence the page has issued so far; a batch with no operations only reports it,
/// and <c>"left": true</c> marks the report a page sends once it has left, after which it issues nothing more),
/// answered with <c>{ "ack": n }</c> — the highest sequence from that tab now on disk. Operations at or below
/// the tab's acknowledged sequence are skipped, so a batch can be resent any number of times. A request must
/// carry the scope's session cookie and the request header; a page on another origin can send neither (the
/// cookie belongs to this origin, and the custom header would need a CORS preflight that is never granted).
/// </remarks>
public sealed partial class StorageChannel
{
    private static readonly Lazy<string> ScriptTemplate = new(() =>
    {
        using var stream = typeof(StorageChannel).Assembly.GetManifestResourceStream("LocalOrigin.AspNetCore.storage-channel.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <param name="sessions">Who may write to which scope.</param>
    /// <param name="options">Names on the wire, and what the host is told.</param>
    public StorageChannel(ChannelSessions sessions, StorageChannelOptions? options = null)
    {
        Sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        Options = options ?? new StorageChannelOptions();
    }

    /// <summary>Who may write to which scope.</summary>
    public ChannelSessions Sessions { get; }

    /// <summary>Names on the wire, and what the host is told.</summary>
    public StorageChannelOptions Options { get; }

    /// <summary>
    /// Issues a session (as a cookie on <paramref name="context"/>'s response) and a tab of <paramref name="scope"/>
    /// for a document about to be served, and returns the script to place in front of it, seeded with
    /// <paramref name="items"/>. The script is ASCII and carries the items so they can never close its element.
    /// </summary>
    public ChannelPage Open(HttpContext context, string scope, IReadOnlyDictionary<string, string> items)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        var tab = Sessions.IssueTab(scope);
        context.Response.Cookies.Append(Options.SessionCookie, Sessions.IssueSession(scope), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
        return new ChannelPage(tab, Script(tab, items));
    }

    /// <summary>
    /// The script for a page whose writes go nowhere a session is needed for — a preview, answered by
    /// <see cref="DiscardAsync"/>. <paramref name="tab"/> is any name the host gives it.
    /// </summary>
    public string Script(string tab, IReadOnlyDictionary<string, string> items)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(items);
        var boot = JsonSerializer.Serialize(new Boot(Options.Path, Options.RequestHeader, tab, items, Options.HandleName), BootJson.Default.Boot);
        return ScriptTemplate.Value.Replace("__LOCAL_ORIGIN_BOOT__", boot, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="request"/> carries the header only the page's own script sends, and — when the
    /// browser names an origin — that origin is the one it was sent to.
    /// </summary>
    public bool IsFromThePage(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers[Options.RequestHeader] == "1" && SameOriginOrAbsent(request);
    }

    /// <summary>Whether <paramref name="context"/> carries a session of <paramref name="scope"/>, from a page of the origin it was sent to.</summary>
    public bool IsFromTheScope(HttpContext context, string scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Sessions.IsSession(scope, context.Request.Cookies[Options.SessionCookie]) && SameOriginOrAbsent(context.Request);
    }

    /// <summary>The tab named by a request, if it and the request's session both belong to <paramref name="scope"/>.</summary>
    public ChannelTab? TabOf(HttpContext context, string scope, string? tabId)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Sessions.Authorize(scope, context.Request.Cookies[Options.SessionCookie], tabId);
    }

    /// <summary>Receives a batch of writes for <paramref name="scope"/> and answers with its acknowledgement.</summary>
    /// <param name="context">The request, addressed to <see cref="StorageChannelOptions.Path"/> of <paramref name="scope"/>'s origin.</param>
    /// <param name="scope">The scope the request's origin belongs to.</param>
    /// <param name="store">The scope's store; asked for only once the request is authorized.</param>
    public async Task HandleAsync(HttpContext context, string scope, Func<CancellationToken, ValueTask<KeyValueStore>> store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(store);
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsPost(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (!IsFromThePage(request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var batch = await ReadBatchAsync(request, context.RequestAborted).ConfigureAwait(false);
        var operations = batch?.Ops?.Select(ToOperation).ToList();
        if (batch?.Tab is null || operations is null || operations.Any(o => o.Operation is null))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var logger = context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger<StorageChannel>() ?? (ILogger)NullLogger.Instance;
        var tab = TabOf(context, scope, batch.Tab);
        if (tab is null)
        {
            // A page of this scope whose code was replaced while it was still writing: its write is refused —
            // the new code owns the data now — but it was a write the person made, so the host is told.
            if (Sessions.Retired(scope, batch.Tab) is { } retired && operations.Any(o => o.Sequence > retired.LastSequence))
            {
                LogRefusedFromRetiredTab(logger, scope);
                Options.RefusedFromRetiredTab?.Invoke(context, scope, retired);
            }

            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var target = await store(context.RequestAborted).ConfigureAwait(false);
        await tab.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (batch.Left == true) tab.Left = true;
            tab.Issued = Math.Max(tab.Issued, Math.Max(batch.Issued ?? 0, operations.Count > 0 ? operations.Max(o => o.Sequence) : 0));
            var fresh = operations.Where(o => o.Sequence > tab.LastSequence).OrderBy(o => o.Sequence).ToList();
            if (fresh.Count > 0)
            {
                if (fresh[0].Sequence != tab.LastSequence + 1)
                    LogSequenceGap(logger, scope, tab.LastSequence, fresh[0].Sequence);

                // Not cancelled with the request: once a batch is accepted it is written through, so a page that
                // stops waiting (a closing window) cannot leave it half-applied.
                await target.ApplyAsync(fresh.Select(o => o.Operation!).ToList(), CancellationToken.None).ConfigureAwait(false);
                tab.LastSequence = fresh[^1].Sequence;
                Options.Applied?.Invoke(context, scope, fresh.Count);
            }

            await WriteAckAsync(response, tab.LastSequence, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            tab.Gate.Release();
        }
    }

    /// <summary>
    /// Answers a batch of writes as if it were applied, so the page goes on as it would, and keeps none —
    /// for a preview, whose writes must not reach the real data.
    /// </summary>
    public async Task DiscardAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) || !IsFromThePage(request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var batch = await ReadBatchAsync(request, context.RequestAborted).ConfigureAwait(false);
        if (batch?.Ops is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await WriteAckAsync(context.Response, batch.Ops.Count > 0 ? batch.Ops.Max(o => o.Seq) : 0, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<Batch?> ReadBatchAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(request.Body, ChannelJson.Default.Batch, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task WriteAckAsync(HttpResponse response, long ack, CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        return response.WriteAsync($$"""{"ack":{{ack}}}""", cancellationToken);
    }

    private static bool SameOriginOrAbsent(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static (long Sequence, KeyValueOperation? Operation) ToOperation(WireOperation wire) =>
        (wire.Seq, wire.Op switch
        {
            "set" when wire.Key is not null && wire.Value is not null => KeyValueOperation.Set(wire.Key, wire.Value),
            "remove" when wire.Key is not null => KeyValueOperation.Remove(wire.Key),
            "clear" => KeyValueOperation.Clear(),
            _ => null,
        } is { } operation && wire.Seq > 0 ? operation : null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A page of scope {Scope} running replaced code wrote after the replacement; the write was refused.")]
    private static partial void LogRefusedFromRetiredTab(ILogger logger, string scope);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage operations from a tab of scope {Scope} skipped from sequence {Last} to {First}; operations may have been lost in transit.")]
    private static partial void LogSequenceGap(ILogger logger, string scope, long last, long first);

    internal sealed record Batch(string? Tab, List<WireOperation>? Ops, long? Issued = null, bool? Left = null);

    internal sealed record WireOperation(long Seq, string? Op, string? Key, string? Value);

    internal sealed record Boot(string Endpoint, string Header, string Tab, IReadOnlyDictionary<string, string> Items, string Handle);
}

/// <summary>A document's share of the storage channel: its tab, and the script to place in front of it.</summary>
/// <param name="Tab">The tab issued for the document.</param>
/// <param name="Script">The script — no <c>&lt;script&gt;</c> element around it; ASCII.</param>
public sealed record ChannelPage(string Tab, string Script);

/// <summary>Names on the wire of the <see cref="StorageChannel"/>, and what the host is told.</summary>
public sealed record StorageChannelOptions
{
    /// <summary>The path, on every scope's origin, the page's script writes to.</summary>
    public string Path { get; init; } = "/.local-origin/storage";

    /// <summary>The custom header the page's script sends (with the value <c>1</c>), which another origin cannot send without a preflight.</summary>
    public string RequestHeader { get; init; } = "X-Local-Origin";

    /// <summary>The name of the HTTP-only session cookie.</summary>
    public string SessionCookie { get; init; } = "local_origin_session";

    /// <summary>
    /// The global the script defines for the host to read before it closes the page:
    /// <c>{ tab, issued(), arm() }</c> — the tab, the last sequence the page issued, and a call that makes the
    /// page report that sequence once it has left (see <see cref="ChannelTab.Issued"/> and <see cref="ChannelTab.Left"/>).
    /// Neither writable nor configurable, so the page's own code cannot change what the host reads.
    /// </summary>
    public string HandleName { get; init; } = "__localOrigin";

    /// <summary>Called after operations from a page were applied: the request, the scope and how many.</summary>
    public Action<HttpContext, string, int>? Applied { get; init; }

    /// <summary>
    /// Called when a page of a scope whose sessions were revoked (<see cref="ChannelSessions.Revoke"/>) sent writes
    /// that were never applied: the request, the scope and the retired tab. The writes are refused.
    /// </summary>
    public Action<HttpContext, string, ChannelTab>? RefusedFromRetiredTab { get; init; }
}

/// <summary>Serialization of the channel's wire data.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StorageChannel.Batch))]
internal sealed partial class ChannelJson : JsonSerializerContext;

/// <summary>
/// Serialization of the boot data. The default encoder escapes every non-ASCII character and the
/// HTML-sensitive ones, so stored values can never close the surrounding script element and the result is ASCII.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StorageChannel.Boot))]
internal sealed partial class BootJson : JsonSerializerContext;
