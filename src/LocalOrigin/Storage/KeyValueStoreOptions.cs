namespace LocalOrigin.Storage;

/// <summary>Tuning for <see cref="KeyValueStore"/>. The defaults suit a single desktop user.</summary>
public sealed record KeyValueStoreOptions
{
    /// <summary>
    /// Number of journaled operations after which the next write also rewrites the snapshot and
    /// empties the journal. Smaller values mean faster startup and more snapshot writes.
    /// </summary>
    public int CheckpointEvery { get; init; } = 1000;

    /// <summary>
    /// The journal is also folded into the snapshot once it holds at least this many bytes and at least
    /// <see cref="CheckpointJournalGrowth"/> times the snapshot's size — whichever of this and
    /// <see cref="CheckpointEvery"/> comes first. A page that rewrites one large value on every change
    /// (a whole list stored under one key) otherwise keeps up to <see cref="CheckpointEvery"/> copies of
    /// that value on disk and replays them all on the next open.
    /// </summary>
    public long CheckpointJournalBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// How many times the snapshot's size the journal may grow to before it is folded, once it is past
    /// <see cref="CheckpointJournalBytes"/>. Bounds the storage on disk to a few times the data it holds.
    /// </summary>
    public double CheckpointJournalGrowth { get; init; } = 2;

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
