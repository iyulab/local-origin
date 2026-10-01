using System.Net;
using System.Net.Sockets;
using LocalOrigin.Origins;

namespace LocalOrigin.Tests.Origins;

/// <summary>An origin includes its port, so a listener keeps its port across starts.</summary>
public sealed class RememberedPortTests : IDisposable
{
    private static readonly RememberedPortOptions Quick = new() { Attempts = 3, RetryDelay = TimeSpan.FromMilliseconds(100) };

    private readonly string _directory = Directory.CreateTempSubdirectory("local-origin-ports-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, "ports.json");

    private static Task<TcpListener> Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            listener.Dispose();
            throw new IOException("The port is taken.", exception);
        }

        return Task.FromResult(listener);
    }

    private static int PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private Task<RememberedPortResult<TcpListener>> StartAsync(string key = "main", RememberedPortOptions? options = null) =>
        RememberedPort.StartAsync(new PortMemoryFile(FilePath), key, Listen, PortOf, options ?? Quick);

    [Fact]
    public async Task A_listener_starts_on_the_same_port_after_a_restart()
    {
        var first = await StartAsync();
        first.Listener.Stop();

        var second = await StartAsync();
        try
        {
            Assert.Equal(first.Port, second.Port);
            Assert.Null(first.PreviousPort);
            Assert.Null(second.PreviousPort);
        }
        finally
        {
            second.Listener.Stop();
        }
    }

    [Fact]
    public async Task When_the_remembered_port_stays_taken_a_new_one_is_used_remembered_and_reported()
    {
        var first = await StartAsync();
        first.Listener.Stop();

        using var squatter = new TcpListener(IPAddress.Loopback, first.Port);
        squatter.Start();
        var second = await StartAsync();
        try
        {
            Assert.NotEqual(first.Port, second.Port);
            Assert.Equal(first.Port, second.PreviousPort);
            Assert.Equal(second.Port, new PortMemoryFile(FilePath).Recall("main"));
        }
        finally
        {
            second.Listener.Stop();
            squatter.Stop();
        }
    }

    [Fact]
    public async Task A_port_released_moments_later_is_still_reused()
    {
        var first = await StartAsync();
        first.Listener.Stop();

        var stopping = new TcpListener(IPAddress.Loopback, first.Port);
        stopping.Start();
        var release = Task.Delay(150).ContinueWith(_ => stopping.Stop(), TaskScheduler.Default);
        var second = await StartAsync(options: new RememberedPortOptions { Attempts = 10, RetryDelay = TimeSpan.FromMilliseconds(100) });
        try
        {
            await release;
            Assert.Equal(first.Port, second.Port);
            Assert.Null(second.PreviousPort);
        }
        finally
        {
            second.Listener.Stop();
            stopping.Dispose();
        }
    }

    [Fact]
    public async Task Each_key_keeps_its_own_port()
    {
        var notes = await StartAsync("notes");
        var board = await StartAsync("board");
        notes.Listener.Stop();
        board.Listener.Stop();

        var memory = new PortMemoryFile(FilePath);
        Assert.Equal(notes.Port, memory.Recall("notes"));
        Assert.Equal(board.Port, memory.Recall("board"));
        Assert.NotEqual(notes.Port, board.Port);
    }

    [Fact]
    public async Task An_unreadable_record_is_replaced_by_a_new_port()
    {
        await File.WriteAllTextAsync(FilePath, "{ not json");

        var started = await StartAsync();
        started.Listener.Stop();

        Assert.Null(started.PreviousPort);
        Assert.Equal(started.Port, new PortMemoryFile(FilePath).Recall("main"));
    }

    [Fact]
    public void The_record_is_small_readable_json_with_a_format()
    {
        new PortMemoryFile(FilePath).Remember("main", 51234);

        Assert.Equal("{\n  \"format\": \"local-origin.ports/0\",\n  \"ports\": {\n    \"main\": 51234\n  }\n}\n",
            File.ReadAllText(FilePath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_record_in_another_format_is_not_read()
    {
        new PortMemoryFile(FilePath, "example.host/1").Remember("main", 51234);

        Assert.Null(new PortMemoryFile(FilePath).Recall("main"));
        Assert.Equal(51234, new PortMemoryFile(FilePath, "example.host/1").Recall("main"));
    }
}
