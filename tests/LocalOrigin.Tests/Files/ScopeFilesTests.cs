using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalOrigin.AspNetCore.Files;
using LocalOrigin.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace LocalOrigin.Tests.Files;

/// <summary>
/// A host serving two scopes' folders, <c>notes</c> and <c>board</c>. In each, <c>closed/</c> is not served, only
/// <c>pages/</c> renders as pages, and pages get a marker script in front of their content.
/// </summary>
public sealed class ScopeFilesTests : IAsyncLifetime
{
    private const string Marker = "<script>/*host*/</script>";

    private readonly string _directory = Directory.CreateTempSubdirectory("local-origin-serve-").FullName;
    private readonly Dictionary<string, ScopeFolder> _folders = new(StringComparer.Ordinal);
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        foreach (var scope in new[] { "notes", "board" })
        {
            var root = Path.Combine(_directory, scope);
            Directory.CreateDirectory(Path.Combine(root, "pages", "app"));
            Directory.CreateDirectory(Path.Combine(root, "closed"));
            await File.WriteAllTextAsync(Path.Combine(root, "pages", "app", "index.html"), $"<!doctype html><p>{scope}</p>");
            await File.WriteAllTextAsync(Path.Combine(root, "pages", "app", "data.json"), """{"n":1}""");
            await File.WriteAllTextAsync(Path.Combine(root, "readme.html"), "<p>not a page</p>");
            await File.WriteAllTextAsync(Path.Combine(root, "closed", "secret.txt"), "secret");
            await File.WriteAllTextAsync(Path.Combine(root, "numbers.txt"), "0123456789");
            _folders[scope] = new ScopeFolder(root);
        }

        var files = new ScopeFiles(new ScopeFileOptions
        {
            Serves = request => ValueTask.FromResult(!request.Path.Relative.StartsWith("closed/", StringComparison.Ordinal)),
            RendersAsPage = request => ValueTask.FromResult(request.Path.Relative.StartsWith("pages/", StringComparison.Ordinal)),
            Inject = _ => ValueTask.FromResult<string?>(Marker),
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Run(context =>
        {
            var scope = context.Request.Host.Host.Split('.')[0];
            return files.HandleAsync(context, scope, _folders[scope]);
        });
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<HttpResponseMessage> SendAsync(string scope, string path, HttpMethod? method = null, Action<HttpRequestMessage>? configure = null)
    {
        var client = _app.GetTestClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri($"http://{scope}.localhost{path}"));
        configure?.Invoke(request);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<string> BodyOf(HttpResponseMessage response) => response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_page_is_served_from_its_own_folder_with_the_host_markup_in_front()
    {
        using var notes = await SendAsync("notes", "/pages/app/");
        using var board = await SendAsync("board", "/pages/app/index.html");

        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        Assert.Equal($"<!doctype html>{Marker}<p>notes</p>", await BodyOf(notes));
        Assert.Equal("text/html", notes.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", notes.Content.Headers.ContentType.CharSet);
        Assert.Equal($"<!doctype html>{Marker}<p>board</p>", await BodyOf(board));
        Assert.Equal("<!doctype html><p>notes</p>", await File.ReadAllTextAsync(Path.Combine(_directory, "notes", "pages", "app", "index.html"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_response_carries_the_security_profile()
    {
        using var page = await SendAsync("notes", "/pages/app/");
        using var missing = await SendAsync("notes", "/nothing.txt");

        foreach (var response in new[] { page, missing })
        {
            Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }

        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_from_another_site_is_refused()
    {
        using var response = await SendAsync("notes", "/pages/app/data.json", configure: r =>
        {
            r.Headers.Add("Sec-Fetch-Site", "same-site");
            r.Headers.Add("Sec-Fetch-Mode", "cors");
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_file_that_does_not_render_as_a_page_is_sandboxed_and_an_attachment()
    {
        using var response = await SendAsync("notes", "/readme.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<p>not a page</p>", await BodyOf(response));
        Assert.Equal("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Data_beside_a_page_is_served_as_stored_with_validators()
    {
        using var first = await SendAsync("notes", "/pages/app/data.json");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("""{"n":1}""", await BodyOf(first));
        Assert.Equal("application/json", first.Content.Headers.ContentType!.MediaType);
        Assert.True(first.Headers.CacheControl!.NoCache);
        var tag = first.Headers.ETag!;

        using var again = await SendAsync("notes", "/pages/app/data.json", configure: r => r.Headers.IfNoneMatch.Add(tag));
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
    }

    [Fact]
    public async Task A_byte_range_is_answered()
    {
        using var response = await SendAsync("notes", "/numbers.txt", configure: r => r.Headers.Range = new RangeHeaderValue(2, 4));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("234", await BodyOf(response));
    }

    [Fact]
    public async Task Head_answers_without_a_body()
    {
        using var response = await SendAsync("notes", "/numbers.txt", HttpMethod.Head);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/closed/secret.txt")]
    [InlineData("/nothing.txt")]
    [InlineData("/numbers.txt.")]
    [InlineData("/numbers.txt:stream")]
    [InlineData("/closed/")]
    [InlineData("/")]
    public async Task A_closed_missing_or_unnameable_file_is_a_plain_404(string path)
    {
        using var response = await SendAsync("notes", path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("secret", await BodyOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_named_without_its_slash_is_redirected_to_it()
    {
        using var response = await SendAsync("notes", "/pages/app?x=1");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/pages/app/?x=1", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Only_reads_are_answered()
    {
        using var response = await SendAsync("notes", "/numbers.txt", HttpMethod.Put, r => r.Content = new StringContent("x", Encoding.UTF8));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("1234567890".Length, (await File.ReadAllTextAsync(Path.Combine(_directory, "notes", "numbers.txt"), TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public void The_directory_index_must_be_a_plain_file_name()
    {
        Assert.Throws<ArgumentException>(() => new ScopeFiles(new ScopeFileOptions { DirectoryIndex = "../x.html" }));
    }
}
