namespace LocalOrigin.Storage;

/// <summary>
/// Thrown by <see cref="KeyValueStore.OpenAsync"/> when another <see cref="KeyValueStore"/> — in this process or another —
/// is already the open writer of the directory. Nothing was read or written. Reading the directory without opening it
/// (<see cref="KeyValueStore.PeekAsync"/>) still works.
/// </summary>
public sealed class StoreInUseException : IOException
{
    /// <summary>Creates the exception for <paramref name="directory"/>.</summary>
    public StoreInUseException(string directory, Exception? innerException = null)
        : base($"The storage in '{directory}' is already open for writing elsewhere.", innerException) => Directory = directory;

    /// <summary>The storage directory that is in use.</summary>
    public string Directory { get; }
}
