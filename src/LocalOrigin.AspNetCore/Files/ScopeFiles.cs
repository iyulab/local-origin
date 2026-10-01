using LocalOrigin.Files;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace LocalOrigin.AspNetCore.Files;

/// <summary>
/// Serves a scope's folder on its origin: <c>GET /data/notes.json</c> answers with <c>data/notes.json</c> of the folder.
/// Every path is confined by <see cref="ScopeFolder.Resolve"/>; the host says which files are served at all and which
/// render as pages; every response carries the security profile. No folder is ever listed.
/// </summary>
/// <remarks>
/// <para>
/// A path ending in <c>/</c> serves the folder's <see cref="ScopeFileOptions.DirectoryIndex"/>; a folder named without
/// the slash is redirected to it, so relative addresses inside the page resolve against the folder. A file that is
/// missing, not served, or names nothing in the folder is a plain <c>404</c> — a page cannot tell a closed file from an
/// absent one. Only <c>GET</c> and <c>HEAD</c> are answered; writing goes through <see cref="FileChannel"/>.
/// </para>
/// <para>
/// A file that does not render as a page (<see cref="ScopeFileOptions.RendersAsPage"/>) is still readable by the
/// origin's own pages, but opening it directly shows nothing active: it is served with <c>Content-Security-Policy:
/// sandbox</c> and as an attachment. Pages get the host's markup in front of their content
/// (<see cref="ScopeFileOptions.Inject"/>, through <see cref="DocumentInjector"/>) — on the served copy only. Other files are
/// served with validators (<c>ETag</c>, <c>Last-Modified</c>) and byte ranges, and are revalidated on every use, since
/// local files change underneath the origin.
/// </para>
/// </remarks>
public sealed class ScopeFiles
{
    /// <param name="options">What is served and how.</param>
    public ScopeFiles(ScopeFileOptions? options = null)
    {
        Options = options ?? new ScopeFileOptions();
        if (!ScopeFolderAccepts(Options.DirectoryIndex))
            throw new ArgumentException("The directory index must be a plain file name.", nameof(options));
    }

    /// <summary>What is served and how.</summary>
    public ScopeFileOptions Options { get; }

    /// <summary>Answers <paramref name="context"/> with the file its path names in <paramref name="folder"/>.</summary>
    /// <param name="context">A request addressed to <paramref name="scope"/>'s origin.</param>
    /// <param name="scope">The scope the request's origin belongs to.</param>
    /// <param name="folder">The scope's folder.</param>
    public async Task HandleAsync(HttpContext context, string scope, ScopeFolder folder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(folder);
        var request = context.Request;
        var response = context.Response;
        Options.Profile.Apply(response);
        if (Options.Profile.Refuses(request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Allow = "GET, HEAD";
            return;
        }

        var path = request.Path.Value is { Length: > 0 } value ? value : "/";
        if (path.EndsWith('/')) path += Options.DirectoryIndex;
        else if (folder.Resolve(path) is { } named && Directory.Exists(named.FullPath))
        {
            response.StatusCode = StatusCodes.Status301MovedPermanently;
            response.Headers.Location = request.PathBase + request.Path + "/" + request.QueryString;
            return;
        }

        if (folder.Resolve(path) is not { } file || !File.Exists(file.FullPath)
            || (Options.Serves is { } serves && !await serves(new ScopeFileRequest(context, scope, file)).ConfigureAwait(false)))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var contentType = Options.ContentTypes.TryGetContentType(file.FullPath, out var known) ? known : "application/octet-stream";
        var asPage = Options.RendersAsPage is not { } renders || await renders(new ScopeFileRequest(context, scope, file)).ConfigureAwait(false);
        response.Headers.CacheControl = "no-cache";

        if (!asPage)
        {
            response.Headers.ContentSecurityPolicy = "sandbox";
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = Path.GetFileName(file.FullPath) }.ToString();
        }
        else if (IsDocument(contentType) && Options.Inject is { } inject
            && await inject(new ScopeFileRequest(context, scope, file)).ConfigureAwait(false) is { } markup)
        {
            var stored = await LocalOrigin.Storage.DurableFile.ReadAsync(file.FullPath, context.RequestAborted).ConfigureAwait(false);
            var served = DocumentInjector.Inject(stored, markup);
            response.ContentType = $"{contentType}; charset={served.Charset}";
            response.ContentLength = served.Body.Length;
            if (!HttpMethods.IsHead(request.Method)) await response.Body.WriteAsync(served.Body, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(file.FullPath);
        var tag = new EntityTagHeaderValue($"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"");
        await TypedResults.PhysicalFile(file.FullPath, contentType, lastModified: info.LastWriteTimeUtc, entityTag: tag, enableRangeProcessing: true)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static bool IsDocument(string contentType) =>
        contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static bool ScopeFolderAccepts(string name) =>
        name.Length > 0 && !name.Contains('/', StringComparison.Ordinal) && name is not "." and not "..";
}

/// <summary>What <see cref="ScopeFiles"/> serves and how.</summary>
public sealed record ScopeFileOptions
{
    /// <summary>The headers of every response, and which requests are refused. The host's one profile.</summary>
    public OriginSecurityProfile Profile { get; init; } = new();

    /// <summary>The file a path ending in <c>/</c> serves. <c>index.html</c> by default.</summary>
    public string DirectoryIndex { get; init; } = "index.html";

    /// <summary>Which files are served at all. <see langword="null"/> (the default): every file the folder holds.</summary>
    public Func<ScopeFileRequest, ValueTask<bool>>? Serves { get; init; }

    /// <summary>
    /// Which files render as pages when opened. <see langword="null"/> (the default): all of them. Any other file is served
    /// sandboxed and as an attachment.
    /// </summary>
    public Func<ScopeFileRequest, ValueTask<bool>>? RendersAsPage { get; init; }

    /// <summary>
    /// The markup to place in front of a page's content (ASCII; see <see cref="DocumentInjector"/>), or <see langword="null"/>
    /// to serve the page as stored. Asked for every HTML document that renders as a page.
    /// </summary>
    public Func<ScopeFileRequest, ValueTask<string?>>? Inject { get; init; }

    /// <summary>How file names map to content types.</summary>
    public IContentTypeProvider ContentTypes { get; init; } = new FileExtensionContentTypeProvider();
}

/// <summary>A file <see cref="ScopeFiles"/> asks the host about.</summary>
/// <param name="Context">The request.</param>
/// <param name="Scope">The scope the request's origin belongs to.</param>
/// <param name="Path">The file, already confined to the scope's folder.</param>
public sealed record ScopeFileRequest(HttpContext Context, string Scope, ScopePath Path);
