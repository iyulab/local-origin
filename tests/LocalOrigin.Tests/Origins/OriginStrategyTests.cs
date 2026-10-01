using System.Net;
using LocalOrigin.AspNetCore;
using LocalOrigin.Origins;
using Microsoft.AspNetCore.Http;

namespace LocalOrigin.Tests.Origins;

public sealed class OriginStrategyTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("notes-2", true)]
    [InlineData("0abc", true)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("A", false)]
    [InlineData("a.b", false)]
    [InlineData("a_b", false)]
    [InlineData("한", false)]
    public void Scope_names_are_dns_labels(string name, bool valid)
    {
        Assert.Equal(valid, ScopeName.IsValid(name));
    }

    [Fact]
    public void A_scope_name_is_at_most_63_characters()
    {
        Assert.True(ScopeName.IsValid(new string('a', 63)));
        Assert.False(ScopeName.IsValid(new string('a', 64)));
    }

    [Fact]
    public void Subdomain_origins_put_the_scope_in_the_host_name()
    {
        var origins = new SubdomainOrigins(51234);

        Assert.Equal(new Uri("http://notes.localhost:51234/"), origins.OriginOf("notes"));
        Assert.Equal("notes", origins.ScopeOf("notes.localhost", 51234));
        Assert.Equal("notes", origins.ScopeOf("NOTES.localhost", 51234));
        Assert.Throws<ArgumentException>(() => origins.OriginOf("Not Valid"));
    }

    [Theory]
    [InlineData("localhost", 51234)]
    [InlineData("a.b.localhost", 51234)]
    [InlineData("notes.example.com", 51234)]
    [InlineData("127.0.0.1", 51234)]
    [InlineData("notes.localhost", 51235)]
    public void Subdomain_origins_name_no_scope_for_other_hosts_or_ports(string host, int port)
    {
        Assert.Null(new SubdomainOrigins(51234).ScopeOf(host, port));
    }

    [Fact]
    public void Port_origins_tell_scopes_apart_by_the_port_a_request_arrived_on()
    {
        var origins = new PortOrigins();
        origins.Bind("notes", 51234);
        origins.Bind("board", 51235);

        Assert.Equal(new Uri("http://127.0.0.1:51234/"), origins.OriginOf("notes"));
        Assert.Equal("notes", origins.ScopeOf("127.0.0.1", 51234));
        Assert.Equal("board", origins.ScopeOf("localhost", 51235));
        Assert.Null(origins.ScopeOf("127.0.0.1", 51236));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("notes.localhost")]
    [InlineData("10.0.0.1")]
    public void Port_origins_name_no_scope_for_a_host_name_that_is_not_their_address(string host)
    {
        var origins = new PortOrigins();
        origins.Bind("notes", 51234);

        Assert.Null(origins.ScopeOf(host, 51234));
    }

    [Fact]
    public void Port_origins_on_an_ipv6_address_bracket_it()
    {
        var origins = new PortOrigins(IPAddress.IPv6Loopback);
        origins.Bind("notes", 51234);

        Assert.Equal(new Uri("http://[::1]:51234/"), origins.OriginOf("notes"));
        Assert.Equal("notes", origins.ScopeOf("[::1]", 51234));
    }

    [Fact]
    public void A_port_belongs_to_one_scope_and_a_rebound_scope_frees_its_old_port()
    {
        var origins = new PortOrigins();
        origins.Bind("notes", 51234);

        Assert.Throws<InvalidOperationException>(() => origins.Bind("board", 51234));

        origins.Bind("notes", 51240);
        origins.Bind("board", 51234);
        Assert.Equal("board", origins.ScopeOf("127.0.0.1", 51234));

        Assert.True(origins.Unbind("board"));
        Assert.Null(origins.ScopeOf("127.0.0.1", 51234));
        Assert.Throws<InvalidOperationException>(() => origins.OriginOf("board"));
        Assert.Equal(new Dictionary<string, int> { ["notes"] = 51240 }, origins.Bound);
    }

    [Fact]
    public void A_request_is_resolved_from_its_host_header_and_local_port()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("notes.localhost", 51234);
        context.Connection.LocalPort = 51234;

        Assert.Equal("notes", new SubdomainOrigins(51234).ScopeOf(context.Request));
    }
}
