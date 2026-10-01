namespace LocalOrigin.Storage;

/// <summary>Tuning for <see cref="KeyValueStore"/>. The defaults suit a single desktop user.</summary>
public sealed record KeyValueStoreOptions
{
    /// <summary>
    /// Number of journaled operations after which the next write also rewrites the snapshot and
    /// empties the journal. Smaller values mean faster startup and more snapshot writes.
    /// </summary>
    public int CheckpointEvery { get; init; } = 1000;

    /// <summary>How many of the most recent previous snapshots are always kept in <c>versions/</c>.</summary>
    public int KeepRecentVersions { get; init; } = 20;

    /// <summary>
    /// For this many days back, the newest previous snapshot of each day is kept in addition to
    /// the most recent ones, so an older state stays recoverable after a burst of writes.
    /// </summary>
    public int KeepDailyVersionsForDays { get; init; } = 30;

    /// <summary>
    /// The format identifier written into every snapshot and required of every snapshot read. A host
    /// that already has snapshots on disk under another identifier keeps writing that identifier, so
    /// its existing data stays readable by every version of the host — including older ones.
    /// </summary>
    public string Format { get; init; } = KeyValueStore.DefaultFormat;

    /// <summary>Clock used to stamp previous snapshots. Tests substitute a fixed clock.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
