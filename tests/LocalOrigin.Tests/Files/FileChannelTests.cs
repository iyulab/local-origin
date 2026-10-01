using System.Net;
using System.Text;
using LocalOrigin.AspNetCore.Files;
using LocalOrigin.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace LocalOrigin.Tests.Files;

/// <summary>
/// A host with two scopes, <c>notes</c> and <c>board</c>, each with a folder of its own. Every request goes to the
/// file channel of the scope its host name names; the host allows writes under <c>saved/</c> only, and
/// <c>preview</c> answers like <c>notes</c> but discards.
/// </summary>
public sealed class FileChannelTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("local-origin-files-").FullName;
    private readonly Dictionary<string, ScopeFolder> _folders = new(StringComparer.Ordinal);
    private readonly List<(string Scope, string Path, long Size, bool Created)> _written = [];
    private WebApplication? _app;
    private FileChannel _channel = null!;

    public async ValueTask InitializeAsync() => await StartAsync(new FileChannelOptions());

    private async Task StartAsync(FileChannelOptions options)
    {
        if (_app is not null) await _app.DisposeAsync();
        foreach (var scope in new[] { "notes", "board" })
        {
            Directory.CreateDirectory(Path.Combine(_directory, scope, "saved"));
            _folders[scope] = new ScopeFolder(Path.Combine(_directory, scope));
        }

        _channel = new FileChannel(options with
        {
            MaxBytes = 16,
            Allows = request => ValueTask.FromResult(request.Path.Relative.StartsWith("saved/", StringComparison.Ordinal)),
            Written = written =>
            {
                lock (_written) _written.Add((written.Scope, written.Path.Relative, written.Size, written.Created));
                return ValueTask.CompletedTask;
            },
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Run(context =>
        {
            var scope = context.Request.Host.Host.Split('.')[0];
            return scope == "preview"
                ? _channel.DiscardAsync(context, scope, _folders["notes"])
                : _channel.HandleAsync(context, scope, _folders[scope]);
        });
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<HttpResponseMessage> PutAsync(string scope, string path, string body, bool header = true, string? origin = null, HttpMethod? method = null)
    {
        var client = _app!.GetTestClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Put, new Uri($"http://{scope}.localhost{path}"))
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        if (header) request.Headers.Add("X-Local-Origin", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    private string FileOf(string scope, string relative) => Path.Combine(_directory, scope, relative);

    [Fact]
    public async Task A_put_creates_then_replaces_the_file_at_its_address()
    {
        using var created = await PutAsync("notes", "/saved/a.json", """{"v":1}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("""{"size":7}""", await created.Content.ReadAsStringAsync());

        using var replaced = await PutAsync("notes", "/saved/a.json", """{"v":22}""");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        Assert.Equal("""{"v":22}""", await File.ReadAllTextAsync(FileOf("notes", "saved/a.json"), TestContext.Current.CancellationToken));
        Assert.Equal([("notes", "saved/a.json", 7L, true), ("notes", "saved/a.json", 8L, false)], _written);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "notes", "saved"), "*.tmp"));
    }

    [Fact]
    public async Task A_scope_writes_only_into_its_own_folder()
    {
        using var response = await PutAsync("board", "/saved/a.json", "board");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(File.Exists(FileOf("board", "saved/a.json")));
        Assert.False(File.Exists(FileOf("notes", "saved/a.json")));
    }

    [Fact]
    public async Task Paths_the_host_does_not_allow_are_refused()
    {
        using var response = await PutAsync("notes", "/index.html", "x");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(FileOf("notes", "index.html")));
        Assert.Empty(_written);
    }

    [Fact]
    public async Task With_no_answer_from_the_host_nothing_is_writable()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "PUT";
        context.Request.Path = "/saved/a.json";
        context.Request.Headers["X-Local-Origin"] = "1";

        await new FileChannel().HandleAsync(context, "notes", _folders["notes"]);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/saved/a.json.")]
    [InlineData("/saved/a.json:stream")]
    [InlineData("/saved/nul")]
    [InlineData("/saved/")]
    public async Task Paths_that_name_nothing_in_the_folder_are_refused(string path)
    {
        using var response = await PutAsync("notes", path, "x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_written);
    }

    [Fact]
    public async Task A_request_without_the_header_or_from_another_origin_is_refused()
    {
        using var bare = await PutAsync("notes", "/saved/a.json", "x", header: false);
        using var foreign = await PutAsync("notes", "/saved/a.json", "x", origin: "http://board.localhost");

        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.False(File.Exists(FileOf("notes", "saved/a.json")));
    }

    [Fact]
    public async Task The_page_itself_naming_its_origin_may_write()
    {
        using var response = await PutAsync("notes", "/saved/a.json", "x", origin: "http://notes.localhost");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Only_put_is_answered()
    {
        using var response = await PutAsync("notes", "/saved/a.json", "x", method: HttpMethod.Post);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("PUT", response.Content.Headers.Allow.Single());
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_and_the_file_kept()
    {
        using var first = await PutAsync("notes", "/saved/a.json", "old");

        using var response = await PutAsync("notes", "/saved/a.json", new string('x', 17));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("old", await File.ReadAllTextAsync(FileOf("notes", "saved/a.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_folder_is_created_only_when_the_host_lets_it()
    {
        using var refused = await PutAsync("notes", "/saved/day/a.json", "x");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.False(Directory.Exists(FileOf("notes", "saved/day")));

        await StartAsync(new FileChannelOptions { CreateFolders = true });
        using var created = await PutAsync("notes", "/saved/day/a.json", "x");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(File.Exists(FileOf("notes", "saved/day/a.json")));
    }

    [Fact]
    public async Task A_preview_is_answered_as_if_written_and_keeps_nothing()
    {
        using var response = await PutAsync("preview", "/saved/a.json", "x");
        using var refused = await PutAsync("preview", "/index.html", "x");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(File.Exists(FileOf("notes", "saved/a.json")));
        Assert.Empty(_written);
    }
}
