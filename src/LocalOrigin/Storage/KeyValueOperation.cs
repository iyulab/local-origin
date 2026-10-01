namespace LocalOrigin.Storage;

/// <summary>The kind of change a <see cref="KeyValueOperation"/> makes to a <see cref="KeyValueStore"/>.</summary>
public enum KeyValueOperationKind
{
    /// <summary>Store <c>Value</c> under <c>Key</c>, replacing any previous value.</summary>
    Set,

    /// <summary>Remove <c>Key</c>. Removing an absent key is not an error.</summary>
    Remove,

    /// <summary>Remove every key.</summary>
    Clear,
}

/// <summary>
/// One change to a <see cref="KeyValueStore"/>, mirroring the three mutating calls of the
/// Web Storage API (<c>setItem</c>, <c>removeItem</c>, <c>clear</c>). Keys and values are strings,
/// exactly as that API defines them.
/// </summary>
public sealed record KeyValueOperation
{
    private KeyValueOperation(KeyValueOperationKind kind, string? key, string? value)
    {
        Kind = kind;
        Key = key;
        Value = value;
    }

    /// <summary>What this operation does.</summary>
    public KeyValueOperationKind Kind { get; }

    /// <summary>The key affected; <see langword="null"/> only for <see cref="KeyValueOperationKind.Clear"/>.</summary>
    public string? Key { get; }

    /// <summary>The stored value; non-<see langword="null"/> only for <see cref="KeyValueOperationKind.Set"/>.</summary>
    public string? Value { get; }

    /// <summary>Creates a <see cref="KeyValueOperationKind.Set"/> operation.</summary>
    public static KeyValueOperation Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return new(KeyValueOperationKind.Set, key, value);
    }

    /// <summary>Creates a <see cref="KeyValueOperationKind.Remove"/> operation.</summary>
    public static KeyValueOperation Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(KeyValueOperationKind.Remove, key, null);
    }

    /// <summary>Creates a <see cref="KeyValueOperationKind.Clear"/> operation.</summary>
    public static KeyValueOperation Clear() => new(KeyValueOperationKind.Clear, null, null);

    internal void ApplyTo(IDictionary<string, string> items)
    {
        switch (Kind)
        {
            case KeyValueOperationKind.Set:
                items[Key!] = Value!;
                break;
            case KeyValueOperationKind.Remove:
                items.Remove(Key!);
                break;
            case KeyValueOperationKind.Clear:
                items.Clear();
                break;
        }
    }
}
