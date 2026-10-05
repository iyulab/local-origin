using System.Text.Json;
using System.Text.Json.Serialization;
using LocalOrigin.Previews;
using Microsoft.AspNetCore.Http;

namespace LocalOrigin.AspNetCore.Previews;

/// <summary>
/// Problem reporting: a script placed in front of a page (<see cref="Script"/>) reports the errors its own code
/// throws — while loading, and afterwards — the errors it reports itself with <c>console.error</c>, and what
/// the content security policy refuses, and <see cref="ReceiveAsync"/> takes those reports in. A preview adds them to its <see cref="PreviewReport"/> (<see cref="AddTo"/>); a host can
/// also take them for the pages people use, to tell the person what stopped one.
/// </summary>
/// <remarks>
/// Wire: <c>POST</c> to <see cref="ProblemReportOptions.Path"/> with
/// <c>{ "tab": "...", "kind": "load-error", "message": "..." }</c>,
/// <c>{ "tab": "...", "kind": "error", "message": "..." }</c> or
/// <c>{ "tab": "...", "kind": "blocked", "category": "library" | "data" | "form", "host": "..." }</c>,
/// answered with <c>{}</c> — 200 with a body rather than 204: a fetch answered with 204 was observed to keep a
/// Chromium-family browser from shutting down cleanly.
/// </remarks>
public sealed class ProblemReports
{
    /// <summary>The longest error message kept.</summary>
    public const int MaxMessageLength = 500;

    private static readonly Lazy<string> ScriptTemplate = new(() =>
    {
        using var stream = typeof(ProblemReports).Assembly.GetManifestResourceStream("LocalOrigin.AspNetCore.problem-report.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <param name="options">Names on the wire.</param>
    public ProblemReports(ProblemReportOptions? options = null)
    {
        Options = options ?? new ProblemReportOptions();
    }

    /// <summary>Names on the wire.</summary>
    public ProblemReportOptions Options { get; }

    /// <summary>
    /// The script to place in front of a page (no <c>&lt;script&gt;</c> element around it; ASCII).
    /// </summary>
    /// <param name="tab">A name for the page, sent back with every report.</param>
    /// <param name="lineOffset">How many lines all injected markup adds before the page's own first line, so reported lines match the page's file.</param>
    public string Script(string tab, int lineOffset)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        var boot = JsonSerializer.Serialize(new Boot(Options.Path, Options.RequestHeader, tab, lineOffset), ProblemJson.Default.Boot);
        return ScriptTemplate.Value.Replace("__LOCAL_ORIGIN_PROBLEMS__", boot, StringComparison.Ordinal);
    }

    /// <summary>
    /// Takes in a report sent by the script and answers it. Returns the problem, or <see langword="null"/> when the
    /// request was not a well-formed report from the page itself (it is then answered with an error status).
    /// </summary>
    public async Task<PageProblem?> ReceiveAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsPost(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return null;
        }

        if (!PageRequests.IsFromThePage(request, Options.RequestHeader))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return null;
        }

        Wire? wire;
        try
        {
            wire = await JsonSerializer.DeserializeAsync(request.Body, ProblemJson.Default.Wire, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            wire = null;
        }

        PageProblem? problem = wire switch
        {
            { Tab: not null, Kind: "load-error", Message: { Length: > 0 } message } =>
                new PageLoadError(wire.Tab, Bounded(message)),
            { Tab: not null, Kind: "error", Message: { Length: > 0 } message } =>
                new PageError(wire.Tab, Bounded(message)),
            { Tab: not null, Kind: "blocked", Host: { Length: > 0 and <= 255 } host } when ParseCategory(wire.Category) is { } category =>
                new PageBlocked(wire.Tab, new BlockedRequest(category, host)),
            _ => null,
        };
        if (problem is null)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }

        response.ContentType = "application/json";
        await response.WriteAsync("{}", context.RequestAborted).ConfigureAwait(false);
        return problem;
    }

    /// <summary>Adds <paramref name="problem"/> to <paramref name="report"/>.</summary>
    public static void AddTo(PreviewReport report, PageProblem problem)
    {
        ArgumentNullException.ThrowIfNull(report);
        switch (problem)
        {
            case PageLoadError error: report.AddError(error.Message); break;
            case PageError error: report.AddError(error.Message); break;
            case PageBlocked blocked: report.AddBlocked(blocked.Blocked); break;
            default: throw new ArgumentException("Unknown problem.", nameof(problem));
        }
    }

    private static string Bounded(string message) => message.Length > MaxMessageLength ? message[..MaxMessageLength] : message;

    private static BlockedCategory? ParseCategory(string? category) => category switch
    {
        "library" => BlockedCategory.Library,
        "data" => BlockedCategory.Data,
        "form" => BlockedCategory.Form,
        _ => null,
    };

    internal sealed record Wire(string? Tab, string? Kind, string? Message, string? Category, string? Host);

    internal sealed record Boot(string Endpoint, string Header, string Tab, int LineOffset);
}

/// <summary>A problem a page reported. <see cref="Tab"/> is the name the host gave the page.</summary>
public abstract record PageProblem(string Tab);

/// <summary>An error the page's own code threw while loading, with lines counted as in the page's file.</summary>
public sealed record PageLoadError(string Tab, string Message) : PageProblem(Tab);

/// <summary>
/// An error after the page loaded — one its own code threw (lines counted as in the page's file), or one it
/// reported with <c>console.error</c>, at any time. The page started; something it does went wrong.
/// </summary>
public sealed record PageError(string Tab, string Message) : PageProblem(Tab);

/// <summary>Something the content security policy refused.</summary>
public sealed record PageBlocked(string Tab, BlockedRequest Blocked) : PageProblem(Tab);

/// <summary>Names on the wire of <see cref="ProblemReports"/>.</summary>
public sealed record ProblemReportOptions
{
    /// <summary>The path, on every scope's origin, the script reports to.</summary>
    public string Path { get; init; } = "/.local-origin/problems";

    /// <summary>The custom header the script sends (with the value <c>1</c>), which another origin cannot send without a preflight.</summary>
    public string RequestHeader { get; init; } = "X-Local-Origin";
}

/// <summary>Serialization of the wire and boot data; the boot is ASCII (see <see cref="Storage.BootJson"/>).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProblemReports.Wire))]
[JsonSerializable(typeof(ProblemReports.Boot))]
internal sealed partial class ProblemJson : JsonSerializerContext;
