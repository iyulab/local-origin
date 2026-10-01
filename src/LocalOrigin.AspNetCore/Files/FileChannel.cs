using LocalOrigin.Files;
using LocalOrigin.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace LocalOrigin.AspNetCore.Files;

/// <summary>
/// The file channel: a page saves a file by <c>PUT</c>ting it to its own address — <c>PUT /data/notes.json</c> on
/// the page's origin replaces <c>data/notes.json</c> in the scope's folder. The write is confined to that folder
/// (<see cref="ScopeFolder.Resolve"/>), limited to the paths the host allows, and atomic: the file is replaced
/// only once every byte is on disk, so a crash leaves the old file or the new one, never a part.
/// </summary>
/// <remarks>
/// <para>
/// A request must carry the request header (<see cref="FileChannelOptions.RequestHeader"/>, value <c>1</c>): a page
/// on another origin cannot add it without a CORS preflight, which is never granted. When the browser names an
/// origin, it must be the one the request was sent to.
/// </para>
/// <para>
/// Answers: <c>201</c> when the file was created, <c>200</c> when it was replaced — both with <c>{"size":n}</c> and
/// only once the file is on disk; <c>400</c> for a path that names nothing in the folder; <c>403</c> for a request
/// not from the page, or a path the host does not allow; <c>405</c> for any method but <c>PUT</c>; <c>409</c> when
/// the path's folder does not exist and the host does not let the channel create it; <c>413</c> for a body over
/// <see cref="FileChannelOptions.MaxBytes"/>. A <c>PUT</c> is a whole replacement, so a page that lost the answer
/// sends the same request again and the file ends the same.
/// </para>
/// </remarks>
public sealed class FileChannel
{
    /// <param name="options">The header, the limits and what the host decides.</param>
    public FileChannel(FileChannelOptions? options = null)
    {
        Options = options ?? new FileChannelOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaxBytes);
    }

    /// <summary>The header, the limits and what the host decides.</summary>
    public FileChannelOptions Options { get; }

    /// <summary>Writes the request's body to the file its path names in <paramref name="folder"/>, and answers.</summary>
    /// <param name="context">A request addressed to <paramref name="scope"/>'s origin.</param>
    /// <param name="scope">The scope the request's origin belongs to.</param>
    /// <param name="folder">The scope's folder.</param>
    public Task HandleAsync(HttpContext context, string scope, ScopeFolder folder) => ReceiveAsync(context, scope, folder, write: true);

    /// <summary>
    /// Checks and answers a write exactly as <see cref="HandleAsync"/> would, and keeps nothing — for a preview,
    /// whose writes must not reach the real files. Nothing is reported to <see cref="FileChannelOptions.Written"/>.
    /// </summary>
    public Task DiscardAsync(HttpContext context, string scope, ScopeFolder folder) => ReceiveAsync(context, scope, folder, write: false);

    private async Task ReceiveAsync(HttpContext context, string scope, ScopeFolder folder, bool write)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(folder);
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsPut(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Allow = "PUT";
            return;
        }

        if (!PageRequests.IsFromThePage(request, Options.RequestHeader))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (folder.Resolve(request.Path.Value) is not { } path)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (Options.Allows is not { } allows || !await allows(new FileWriteRequest(context, scope, path)).ConfigureAwait(false))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var directory = Path.GetDirectoryName(path.FullPath)!;
        if (!Directory.Exists(directory) && !Options.CreateFolders)
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (request.ContentLength > Options.MaxBytes)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var created = !File.Exists(path.FullPath);
        if (write)
        {
            Directory.CreateDirectory(directory);
            // Not cancelled with the request: once the body is in, it is written through, so a page that stops
            // waiting (a closing window) cannot leave the write half-done.
            await DurableFile.WriteAtomicallyAsync(path.FullPath, body, CancellationToken.None).ConfigureAwait(false);
            if (Options.Written is { } written) await written(new FileWritten(context, scope, path, body.Length, created)).ConfigureAwait(false);
        }

        response.StatusCode = created ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        response.ContentType = "application/json";
        await response.WriteAsync($$"""{"size":{{body.Length}}}""", context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The body, or <see langword="null"/> when it is longer than <see cref="FileChannelOptions.MaxBytes"/>.</summary>
    private async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit && (limit.MaxRequestBodySize is null || limit.MaxRequestBodySize > Options.MaxBytes))
            limit.MaxRequestBodySize = Options.MaxBytes + 1;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        try
        {
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > Options.MaxBytes) return null;
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return buffer.ToArray();
    }
}

/// <summary>The header, the limits and what the host decides, for the <see cref="FileChannel"/>.</summary>
public sealed record FileChannelOptions
{
    /// <summary>The custom header the page sends (with the value <c>1</c>), which another origin cannot send without a preflight.</summary>
    public string RequestHeader { get; init; } = "X-Local-Origin";

    /// <summary>The largest body accepted, in bytes. 10 MiB by default.</summary>
    public long MaxBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Which files a page may write. Asked for every write after its path is confined to the folder; nothing is
    /// writable until the host says so — with no answer, every write is refused.
    /// </summary>
    public Func<FileWriteRequest, ValueTask<bool>>? Allows { get; init; }

    /// <summary>Whether a write may create the folders its path names inside the scope's folder. Off by default.</summary>
    public bool CreateFolders { get; init; }

    /// <summary>Called after a file is on disk — for a host that has to tell its own writes from others'.</summary>
    public Func<FileWritten, ValueTask>? Written { get; init; }
}

/// <summary>A write the <see cref="FileChannel"/> asks the host about.</summary>
/// <param name="Context">The request.</param>
/// <param name="Scope">The scope the request's origin belongs to.</param>
/// <param name="Path">The file, already confined to the scope's folder.</param>
public sealed record FileWriteRequest(HttpContext Context, string Scope, ScopePath Path);

/// <summary>A file the <see cref="FileChannel"/> wrote.</summary>
/// <param name="Context">The request.</param>
/// <param name="Scope">The scope the request's origin belongs to.</param>
/// <param name="Path">The file.</param>
/// <param name="Size">Its length, in bytes.</param>
/// <param name="Created">Whether it did not exist before.</param>
public sealed record FileWritten(HttpContext Context, string Scope, ScopePath Path, long Size, bool Created);
