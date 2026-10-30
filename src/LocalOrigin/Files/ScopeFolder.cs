using System.Buffers;

namespace LocalOrigin.Files;

/// <summary>
/// A folder a scope's origin serves and writes to, and the one way a request path becomes a file in it.
/// Every file the origin reads or writes is resolved here, so confinement is decided once: a path that could
/// name anything outside the folder — or name it differently on another file system — names nothing.
/// </summary>
/// <remarks>
/// A path is refused when any segment is empty, <c>.</c> or <c>..</c>; holds a backslash, a colon (a drive or
/// an alternate data stream), a wildcard, a control character or a character Windows forbids in names; ends in
/// a dot or a space (Windows drops them, so <c>a.txt.</c> would reach <c>a.txt</c>); or is a reserved device
/// name (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, …, with or without an extension); or has the shape of a Windows short
/// (8.3) name — <c>PROGRA~1</c>, <c>REPORT~1.DOC</c>: such a name can be an alias for a longer one, which the file
/// system resolves when the file is opened, so a name that cannot be told from an alias without asking the file system
/// names nothing (a person's own <c>Report~1.doc</c> is refused with them). The rules apply on every system,
/// so a folder behaves the same wherever it is served. A path whose existing part passes through a link or other
/// reparse point is refused too: the link could lead anywhere.
/// </remarks>
public sealed class ScopeFolder
{
    private static readonly SearchValues<char> Forbidden = SearchValues.Create("\\:*?\"<>|");

    /// <summary>The characters a Windows short name is made of: letters, digits and <c>! # $ % &amp; ' ( ) - @ ^ _ ` { } ~</c> — no space, no other symbol.</summary>
    private static readonly SearchValues<char> ShortNameCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$%&'()-@^_`{}~");

    private static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <param name="root">The folder. It must exist; what it is reached through is the host's choice.</param>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> does not exist.</exception>
    public ScopeFolder(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"The folder '{full}' does not exist.");
        Root = full;
    }

    /// <summary>The folder, as a full path without a trailing separator.</summary>
    public string Root { get; }

    /// <summary>
    /// The file <paramref name="path"/> names in the folder, or <see langword="null"/> when it names nothing the
    /// origin may touch. <paramref name="path"/> is a request path as the server decoded it, with or without its
    /// leading <c>/</c>; segments are separated by <c>/</c> only. The file need not exist.
    /// </summary>
    public ScopePath? Resolve(string? path)
    {
        if (path is null) return null;
        var relative = path.StartsWith('/') ? path[1..] : path;
        if (relative.Length == 0) return null;

        var segments = relative.Split('/');
        foreach (var segment in segments)
            if (!IsAllowedSegment(segment)) return null;

        var full = Path.GetFullPath(Path.Combine([Root, .. segments]));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison)) return null;

        var current = Root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) break;   // the rest does not exist yet, so it cannot be a link
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        }

        return new ScopePath(string.Join('/', segments), full);
    }

    private static bool IsAllowedSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or "..") return false;
        if (segment[^1] is '.' or ' ') return false;
        if (segment.AsSpan().ContainsAny(Forbidden)) return false;
        foreach (var c in segment)
            if (char.IsControl(c)) return false;
        var stem = segment.Split('.')[0].TrimEnd(' ');
        return !DeviceNames.Contains(stem) && !LooksLikeShortName(segment);
    }

    /// <summary>
    /// An alias such as <c>PROGRA~1</c> or <c>FI3A5B~12.TXT</c>: at most one dot, a base of 1–8 characters of the short-name set that ends
    /// in <c>~</c> and digits with at least one character before the <c>~</c>, an extension of at most three characters of the same set.
    /// </summary>
    private static bool LooksLikeShortName(string segment)
    {
        var dot = segment.IndexOf('.');
        if (dot >= 0 && segment.IndexOf('.', dot + 1) >= 0) return false;
        var stem = dot < 0 ? segment.AsSpan() : segment.AsSpan(0, dot);
        var extension = dot < 0 ? default : segment.AsSpan(dot + 1);
        if (stem.Length is < 3 or > 8 || extension.Length > 3) return false;
        if (stem.ContainsAnyExcept(ShortNameCharacters) || extension.ContainsAnyExcept(ShortNameCharacters)) return false;
        var tilde = stem.LastIndexOf('~');
        return tilde >= 1 && tilde < stem.Length - 1 && !stem[(tilde + 1)..].ContainsAnyExceptInRange('0', '9');
    }
}

/// <summary>A file of a <see cref="ScopeFolder"/>, named both ways.</summary>
/// <param name="Relative">The path inside the folder, segments separated by <c>/</c>, no leading <c>/</c>.</param>
/// <param name="FullPath">The full path on disk.</param>
public sealed record ScopePath(string Relative, string FullPath);
