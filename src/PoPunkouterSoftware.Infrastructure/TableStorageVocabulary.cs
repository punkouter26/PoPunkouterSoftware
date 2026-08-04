namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Canonical partition/row key vocabulary for the app's Table Storage entities.
/// The <c>TableEntity(partitionKey, rowKey)</c> constructor takes two indistinguishable
/// strings — routing every key through these members makes a partition/row swap or a
/// hand-rolled tick format a compile-time impossibility instead of a data-corruption bug.
/// </summary>
public static class TablePartitions
{
    public const string Report = "report";
    public const string History = "history";
    public const string HistorySummary = "history-summary";
    public const string Incidents = "incidents";
}

public static class TableRowKeys
{
    public const string Latest = "latest";
}

/// <summary>
/// Row key that sorts newest-first under Table Storage's lexical ordering by encoding the
/// inverse of the timestamp's ticks, zero-padded to a fixed 20 digits.
/// This is the single source of truth for the format: two call sites previously hand-rolled
/// it with different pad widths (D19 vs D20) and different MaxValue bases, which silently
/// breaks ordering once encodings mix in one partition.
/// </summary>
public readonly record struct ReverseChronoRowKey(DateTimeOffset At)
{
    public override string ToString() =>
        (DateTimeOffset.MaxValue.UtcTicks - At.UtcTicks).ToString("D20");

    /// <summary>
    /// Appends a uniqueness suffix for partitions where several rows can share one timestamp
    /// (e.g. multiple incidents detected in the same refresh — DateTime resolution is ~15 ms,
    /// so same-tick collisions are the common case, and an unsuffixed Upsert would silently
    /// keep only the last row).
    /// </summary>
    public string WithSuffix(string suffix) => $"{ToString()}-{suffix}";
}
