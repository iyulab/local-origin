using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LocalOrigin.Storage;

/// <summary>
/// A directory's only writer, held from <see cref="Take"/> until <see cref="Dispose"/>. A second writer — in this process
/// or another on the same computer — is refused with <see cref="DirectoryInUseException"/>.
/// </summary>
/// <remarks>
/// The hold is a small mark file (the directory's full path, computer, process id, the process's start time) created only if
/// none is there, so one creator wins. It is never locked: copying or packaging the directory reads it like any other file. A
/// mark whose process is no longer running — a crash, a killed process — or that was written for another place — a folder
/// copied, packaged and taken in elsewhere, or brought from another computer — is taken over. Disposing
/// removes the mark while it is still this writer's.
/// </remarks>
public sealed class DirectoryWriter : IDisposable
{
    private const string Format = "local-origin.writer/2";

    // Directories this process holds. A second hold in this process is refused without reading the file.
    private static readonly HashSet<string> Held = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _path;
    private readonly string _mark;
    private bool _disposed;

    private DirectoryWriter(string path, string mark)
    {
        _path = path;
        _mark = mark;
    }

    /// <summary>Takes <paramref name="directory"/> (which must exist) as its only writer, marked in <paramref name="markFile"/>.</summary>
    public static DirectoryWriter Take(string directory, string markFile = "writer.lock")
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(markFile);
        var key = Path.GetFullPath(Path.Combine(directory, markFile));
        lock (Held)
        {
            if (!Held.Add(key)) throw new DirectoryInUseException(directory);
        }

        try
        {
            var mark = Own(key);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(key, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        stream.Write(Encoding.UTF8.GetBytes(mark));
                    return new DirectoryWriter(key, mark);
                }
                catch (IOException) when (File.Exists(key) && attempt < 3)
                {
                    string held;
                    try { held = File.ReadAllText(key); }
                    catch (IOException) { continue; }   // the other writer is writing its mark — look again
                    if (IsRunning(held, key)) throw new DirectoryInUseException(directory);
                    try { File.Delete(key); }
                    catch (IOException) { }   // another opener took it over first — CreateNew decides
                }
            }
        }
        catch
        {
            lock (Held) Held.Remove(key);
            throw;
        }
    }

    /// <summary>Gives the directory up: the mark is removed while it is still this writer's.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (File.Exists(_path) && File.ReadAllText(_path) == _mark) File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind, it names a process that has stopped and is taken over at the next take.
        }

        lock (Held) Held.Remove(_path);
    }

    private static string Own(string path)
    {
        using var self = Process.GetCurrentProcess();
        return string.Join('\n', Format, path, Environment.MachineName, self.Id.ToString(CultureInfo.InvariantCulture),
            self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Whether the mark at <paramref name="path"/> was written there by a process still running on this computer (same id and start).</summary>
    private static bool IsRunning(string mark, string path)
    {
        var parts = mark.Split('\n');
        if (parts.Length != 5 || parts[0] != Format
            || !string.Equals(parts[1], path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !string.Equals(parts[2], Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var started)) return false;
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && Math.Abs(process.StartTime.ToUniversalTime().Ticks - started) < TimeSpan.TicksPerSecond;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Thrown by <see cref="DirectoryWriter.Take"/> when another writer — in this process or another — holds the directory.</summary>
public sealed class DirectoryInUseException : IOException
{
    /// <summary>Creates the exception for <paramref name="directory"/>.</summary>
    public DirectoryInUseException(string directory) : base($"'{directory}' is already held by another writer.") => Directory = directory;

    /// <summary>The directory that is held.</summary>
    public string Directory { get; }
}
