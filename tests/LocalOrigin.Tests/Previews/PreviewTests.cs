using System.Text;
using LocalOrigin.AspNetCore.Previews;
using LocalOrigin.Origins;
using LocalOrigin.Previews;
using Microsoft.AspNetCore.Http;

namespace LocalOrigin.Tests.Previews;

public sealed class PreviewTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_preview_gets_a_throwaway_scope_name_that_is_a_valid_origin()
    {
        var previews = new PreviewOrigins<string>();

        var preview = previews.Create("<p>candidate</p>");

        Assert.True(ScopeName.IsValid(preview.Scope));
        Assert.StartsWith("pv-", preview.Scope, StringComparison.Ordinal);
        Assert.True(previews.IsPreviewName(preview.Scope));
        Assert.Same(preview, previews.Find(preview.Scope));
        Assert.Equal("<p>candidate</p>", preview.Content);
        Assert.Equal(new Uri($"http://{preview.Scope}.localhost:51234/"), new SubdomainOrigins(51234).OriginOf(preview.Scope));
        Assert.NotEqual(preview.Scope, previews.Create("other").Scope);
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("pv-0123")]
    [InlineData("pv-0123456789ABCDEF0123456789abcdef")]
    [InlineData("xx-0123456789abcdef0123456789abcdef")]
    public void Other_names_are_not_preview_names(string name)
    {
        Assert.False(new PreviewOrigins<string>().IsPreviewName(name));
        Assert.Null(new PreviewOrigins<string>().Find(name));
    }

    [Fact]
    public void A_preview_lives_for_its_lifetime_only()
    {
        var clock = new Clock();
        var previews = new PreviewOrigins<string>(new PreviewOptions { Lifetime = TimeSpan.FromMinutes(2) }, clock);
        var preview = previews.Create("a");

        clock.Now += TimeSpan.FromMinutes(2);
        Assert.NotNull(previews.Find(preview.Scope));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(previews.Find(preview.Scope));
    }

    [Fact]
    public void Too_many_previews_push_out_the_oldest()
    {
        var clock = new Clock();
        var previews = new PreviewOrigins<int>(new PreviewOptions { MaxPreviews = 2 }, clock);
        var first = previews.Create(1);
        clock.Now += TimeSpan.FromSeconds(1);
        var second = previews.Create(2);
        clock.Now += TimeSpan.FromSeconds(1);
        var third = previews.Create(3);

        Assert.Null(previews.Find(first.Scope));
        Assert.NotNull(previews.Find(second.Scope));
        Assert.NotNull(previews.Find(third.Scope));
        Assert.True(previews.Remove(second.Scope));
        Assert.False(previews.Remove(second.Scope));
    }

    [Fact]
    public void A_name_prefix_that_cannot_make_scope_names_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new PreviewOrigins<int>(new PreviewOptions { NamePrefix = "Preview_" }));
    }

    [Fact]
    public void A_report_keeps_no_repeats_and_stays_bounded()
    {
        var report = new PreviewReport();
        Assert.True(report.IsEmpty);

        report.AddError("x is not defined (line 3)");
        report.AddError("x is not defined (line 3)");
        report.AddBlocked(new BlockedRequest(BlockedCategory.Library, "cdn.example"));
        report.AddBlocked(new BlockedRequest(BlockedCategory.Library, "cdn.example"));
        for (var i = 0; i < 50; i++) report.AddError($"error {i}");

        Assert.Equal("x is not defined (line 3)", report.Errors[0]);
        Assert.Equal([new BlockedRequest(BlockedCategory.Library, "cdn.example")], report.Blocked);
        Assert.Equal(PreviewReport.MaxEntries, report.Errors.Count + report.Blocked.Count);
    }

    private static DefaultHttpContext Post(string body, bool header = true, string? origin = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("pv-x.localhost", 51234);
        if (header) context.Request.Headers["X-Local-Origin"] = "1";
        if (origin is not null) context.Request.Headers.Origin = origin;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task Reports_from_the_page_are_taken_into_the_preview_report()
    {
        var problems = new ProblemReports();
        var report = new PreviewReport();

        var error = await problems.ReceiveAsync(Post("""{"tab":"t","kind":"load-error","message":"boom (line 2)"}"""));
        var blocked = await problems.ReceiveAsync(Post("""{"tab":"t","kind":"blocked","category":"library","host":"cdn.example"}""", origin: "http://pv-x.localhost:51234"));
        ProblemReports.AddTo(report, error!);
        ProblemReports.AddTo(report, blocked!);

        Assert.Equal(new PageLoadError("t", "boom (line 2)"), error);
        Assert.Equal(["boom (line 2)"], report.Errors);
        Assert.Equal([new BlockedRequest(BlockedCategory.Library, "cdn.example")], report.Blocked);
    }

    [Fact]
    public async Task A_report_is_answered_with_a_body()
    {
        var context = Post("""{"tab":"t","kind":"load-error","message":"boom"}""");

        await new ProblemReports().ReceiveAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Equal("{}", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task A_long_message_is_cut()
    {
        var problem = await new ProblemReports().ReceiveAsync(Post($$"""{"tab":"t","kind":"load-error","message":"{{new string('x', 900)}}"}"""));

        Assert.Equal(ProblemReports.MaxMessageLength, ((PageLoadError)problem!).Message.Length);
    }

    [Theory]
    [InlineData("""{"tab":"t","kind":"input"}""", 400)]
    [InlineData("""{"tab":"t","kind":"blocked","category":"other","host":"a"}""", 400)]
    [InlineData("""{"tab":"t","kind":"blocked","category":"data","host":""}""", 400)]
    [InlineData("""{"kind":"load-error","message":"m"}""", 400)]
    [InlineData("not json", 400)]
    public async Task Malformed_reports_are_refused(string body, int status)
    {
        var context = Post(body);

        Assert.Null(await new ProblemReports().ReceiveAsync(context));
        Assert.Equal(status, context.Response.StatusCode);
    }

    [Fact]
    public async Task Reports_without_the_header_or_from_another_origin_are_refused()
    {
        const string body = """{"tab":"t","kind":"load-error","message":"m"}""";
        var noHeader = Post(body, header: false);
        var otherOrigin = Post(body, origin: "http://notes.localhost:51234");

        Assert.Null(await new ProblemReports().ReceiveAsync(noHeader));
        Assert.Null(await new ProblemReports().ReceiveAsync(otherOrigin));
        Assert.Equal(403, noHeader.Response.StatusCode);
        Assert.Equal(403, otherOrigin.Response.StatusCode);
    }

    [Fact]
    public void The_script_is_ascii_and_carries_the_line_offset()
    {
        var script = new ProblemReports().Script("t", 7);

        Assert.True(Ascii.IsValid(script));
        Assert.Contains("\"lineOffset\":7", script, StringComparison.Ordinal);
        Assert.Contains("\"endpoint\":\"/.local-origin/problems\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__LOCAL_ORIGIN_PROBLEMS__", script, StringComparison.Ordinal);
    }
}
