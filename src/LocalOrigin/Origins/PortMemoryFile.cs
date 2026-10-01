using System.Text.Json;

namespace LocalOrigin.Origins;

/// <summary>
/// Ports remembered in one small JSON file: <c>{ "format": "local-origin.ports/0", "ports": { "key": 51234 } }</c>.
/// Written aside and moved into place, so a reader never sees half a file. A file that cannot be read
/// counts as empty: new ports are chosen and remembered.
/// </summary>
/// <param name="path">The file.</param>
/// <param name="format">The format identifier written and required on reading.</param>
public sealed class PortMemoryFile(string path, string format = PortMemoryFile.DefaultFormat) : IPortMemory
{
    /// <summary>The format identifier used unless the host gives another.</summary>
    public const string DefaultFormat = "local-origin.ports/0";

    private readonly Lock _lock = new();

    /// <summary>The file.</summary>
    public string Path { get; } = !string.IsNullOrEmpty(path) ? path : throw new ArgumentException("A path is required.", nameof(path));

    /// <inheritdoc />
    public int? Recall(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock) return Read().TryGetValue(key, out var port) ? port : null;
    }

    /// <inheritdoc />
    public void Remember(string key, int port)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        lock (_lock)
        {
            var ports = Read();
            if (ports.TryGetValue(key, out var known) && known == port) return;
            ports[key] = port;
            Write(ports);
        }
    }

    private SortedDictionary<string, int> Read()
    {
        var ports = new SortedDictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("format", out var tag) && tag.ValueEquals(format)
                && root.TryGetProperty("ports", out var entries) && entries.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in entries.EnumerateObject())
                    if (entry.Value.TryGetInt32(out var port) && port is > 0 and <= 65535)
                        ports[entry.Name] = port;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // No file yet, or one that cannot be used.
        }

        return ports;
    }

    private void Write(SortedDictionary<string, int> ports)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (directory is not null) Directory.CreateDirectory(directory);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", format);
            writer.WriteStartObject("ports");
            foreach (var (key, port) in ports) writer.WriteNumber(key, port);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        var aside = Path + ".tmp";
        File.WriteAllBytes(aside, buffer.ToArray());
        File.Move(aside, Path, overwrite: true);
    }
}
