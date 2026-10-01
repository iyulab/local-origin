namespace LocalOrigin.Origins;

/// <summary>
/// The name of a scope — a page, or a group of pages the host treats as one. A name is a DNS label
/// (lowercase ASCII letters, digits and inner hyphens, at most 63 characters), so it can stand as a
/// subdomain under any strategy and in a file name on any system.
/// </summary>
public static class ScopeName
{
    /// <summary>The longest name: the longest DNS label.</summary>
    public const int MaxLength = 63;

    /// <summary>Whether <paramref name="name"/> is a valid scope name.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength || name[0] == '-' || name[^1] == '-') return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') return false;
        return true;
    }
}
