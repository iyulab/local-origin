using LocalOrigin.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace LocalOrigin.Tests.Serving;

public sealed class OriginSecurityProfileTests
{
    [Fact]
    public void Every_header_of_the_profile_is_set()
    {
        var context = new DefaultHttpContext();

        new OriginSecurityProfile().Apply(context.Response);

        var headers = context.Response.Headers;
        Assert.Equal(OriginSecurityProfile.DefaultContentSecurityPolicy, headers.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'self'", headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Equal("same-origin", headers["Cross-Origin-Resource-Policy"]);
        Assert.Equal("nosniff", headers.XContentTypeOptions);
        Assert.False(headers.ContainsKey("Cross-Origin-Opener-Policy"));
    }

    [Fact]
    public void The_host_chooses_the_content_security_policy()
    {
        var context = new DefaultHttpContext();

        new OriginSecurityProfile { ContentSecurityPolicy = "default-src 'none'" }.Apply(context.Response);

        Assert.Equal("default-src 'none'", context.Response.Headers.ContentSecurityPolicy);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("same-origin", "cors", false)]
    [InlineData("none", "navigate", false)]
    [InlineData("cross-site", "navigate", false)]
    [InlineData("cross-site", "no-cors", true)]
    [InlineData("cross-site", "cors", true)]
    [InlineData("same-site", "no-cors", true)]
    [InlineData("same-site", "cors", true)]
    public void Requests_from_other_sites_are_refused_except_navigations(string? site, string? mode, bool refused)
    {
        var context = new DefaultHttpContext();
        if (site is not null) context.Request.Headers["Sec-Fetch-Site"] = site;
        if (mode is not null) context.Request.Headers["Sec-Fetch-Mode"] = mode;

        Assert.Equal(refused, new OriginSecurityProfile().Refuses(context.Request));
        Assert.False(new OriginSecurityProfile { RefuseCrossSiteRequests = false }.Refuses(context.Request));
    }
}
