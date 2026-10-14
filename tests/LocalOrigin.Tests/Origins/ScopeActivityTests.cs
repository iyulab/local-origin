using LocalOrigin.AspNetCore;
using LocalOrigin.Origins;
using LocalOrigin.Tests.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace LocalOrigin.Tests.Origins;

public sealed class ScopeActivityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_request_is_pending_until_its_handle_is_disposed_once()
    {
        var clock = new ManualClock(Start);
        var activity = new ScopeActivity(clock);

        var first = activity.Begin("notes");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var second = activity.Begin("notes");
        Assert.Equal(new ScopeActivityState(2, Start.AddMilliseconds(10)), activity.Read("notes"));

        clock.Advance(TimeSpan.FromMilliseconds(30));
        first.Dispose();
        first.Dispose();
        Assert.Equal(new ScopeActivityState(1, Start.AddMilliseconds(40)), activity.Read("notes"));

        clock.Advance(TimeSpan.FromMilliseconds(5));
        second.Dispose();
        Assert.Equal(new ScopeActivityState(0, Start.AddMilliseconds(45)), activity.Read("notes"));
    }

    [Fact]
    public void A_scope_never_requested_reads_as_unknown()
    {
        Assert.Null(new ScopeActivity().Read("notes"));
    }

    [Fact]
    public void Settled_means_few_enough_in_flight_for_long_enough()
    {
        var idle = new ScopeActivityState(0, Start);
        var streaming = new ScopeActivityState(1, Start);
        var quiet = TimeSpan.FromMilliseconds(500);

        Assert.False(idle.IsSettled(quiet, Start.AddMilliseconds(499)));
        Assert.True(idle.IsSettled(quiet, Start.AddMilliseconds(500)));
        Assert.False(streaming.IsSettled(quiet, Start.AddSeconds(5)));
        Assert.True(streaming.IsSettled(quiet, Start.AddSeconds(5), allowPending: 1));
    }

    [Fact]
    public void Past_the_bound_the_longest_idle_scope_is_forgotten_and_a_busy_one_never()
    {
        var clock = new ManualClock(Start);
        var activity = new ScopeActivity(clock, maxScopes: 2);

        var busy = activity.Begin("busy");
        clock.Advance(TimeSpan.FromSeconds(1));
        activity.Begin("old").Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        activity.Begin("new").Dispose();

        Assert.NotNull(activity.Read("busy"));
        Assert.Null(activity.Read("old"));
        Assert.NotNull(activity.Read("new"));

        clock.Advance(TimeSpan.FromSeconds(1));
        var third = activity.Begin("third");
        Assert.NotNull(activity.Read("busy"));
        Assert.Null(activity.Read("new"));
        busy.Dispose();
        third.Dispose();
    }

    [Fact]
    public void A_scope_with_a_request_in_flight_is_not_forgotten_on_request()
    {
        var activity = new ScopeActivity();
        var request = activity.Begin("notes");

        Assert.False(activity.Forget("notes"));
        request.Dispose();
        Assert.True(activity.Forget("notes"));
        Assert.Null(activity.Read("notes"));
    }

    [Fact]
    public async Task The_scope_pipeline_counts_a_request_until_its_handler_is_done()
    {
        var activity = new ScopeActivity();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseScopeOrigins(new OneScope(), async (context, scope) =>
        {
            entered.SetResult();
            await release.Task;
        }, activity: activity);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var sending = app.GetTestClient().GetAsync(new Uri("http://notes.localhost/data.json"), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, activity.Read("notes")?.Pending);

        release.SetResult();
        using var response = await sending;
        Assert.Equal(0, activity.Read("notes")?.Pending);
        Assert.Null(activity.Read("other"));
    }

    private sealed class OneScope : IOriginStrategy
    {
        public Uri OriginOf(string scope) => new($"http://{scope}.localhost/");
        public string? ScopeOf(string hostName, int localPort) => hostName == "notes.localhost" ? "notes" : null;
        public bool OwnsPort(int localPort) => false;
    }
}
