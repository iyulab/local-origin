using System.Diagnostics;
using System.Globalization;

namespace LocalOrigin.Storage;

/// <summary>Who holds a storage directory as its writer: computer, process id and the process's start time.</summary>
internal static class WriterMark
{
    private const string Format = "local-origin.writer/1";

    /// <summary>The mark of this process.</summary>
    public static string Own()
    {
        using var self = Process.GetCurrentProcess();
        return string.Join('\n', Format, Environment.MachineName, self.Id.ToString(CultureInfo.InvariantCulture),
            self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether the process a mark names is still running on this computer. A mark that cannot be read, names another
    /// computer, or names a process that has ended or a different process under a reused id, is not running.
    /// </summary>
    public static bool IsRunning(string mark)
    {
        var parts = mark.Split('\n');
        if (parts.Length != 4 || parts[0] != Format || !string.Equals(parts[1], Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var started)) return false;
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
