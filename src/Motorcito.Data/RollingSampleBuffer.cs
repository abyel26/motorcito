namespace Motorcito.Data;

/// <summary>
/// Keeps the last N minutes of samples in memory at full resolution.
///
/// This is what makes Motorcito's event capture better than a freeze frame.
/// Most ECUs store a single freeze frame and overwrite it with the next fault —
/// one instant, no lead-up. When a DTC or a rule fires, this buffer is what
/// lets the app persist the two minutes *before* it, so the event screen can
/// show how conditions developed rather than just where they ended up.
///
/// Bounded by time, not count, so it behaves the same whether the adapter is
/// managing 2 Hz or 20 Hz.
/// </summary>
public sealed class RollingSampleBuffer
{
    private readonly Queue<Sample> _samples = new();
    private readonly object _lock = new();

    public TimeSpan Window { get; }

    public RollingSampleBuffer(TimeSpan? window = null)
        => Window = window ?? TimeSpan.FromMinutes(5);

    public int Count
    {
        get { lock (_lock) return _samples.Count; }
    }

    public void Add(Sample sample)
    {
        lock (_lock)
        {
            _samples.Enqueue(sample);
            Trim(sample.Timestamp);
        }
    }

    /// <summary>
    /// Everything currently retained, oldest first.
    /// </summary>
    public IReadOnlyList<Sample> Snapshot()
    {
        lock (_lock) return _samples.ToArray();
    }

    /// <summary>
    /// The samples around a moment — the window persisted when an event fires.
    /// Defaults match ROADMAP: two minutes before, one minute after.
    /// </summary>
    public IReadOnlyList<Sample> Around(DateTime instant, TimeSpan? before = null, TimeSpan? after = null)
    {
        var from = instant - (before ?? TimeSpan.FromMinutes(2));
        var to = instant + (after ?? TimeSpan.FromMinutes(1));

        lock (_lock)
            return _samples.Where(s => s.Timestamp >= from && s.Timestamp <= to).ToArray();
    }

    public void Clear()
    {
        lock (_lock) _samples.Clear();
    }

    /// <summary>Drops anything older than the window relative to the newest sample.</summary>
    private void Trim(DateTime newest)
    {
        var cutoff = newest - Window;
        while (_samples.Count > 0 && _samples.Peek().Timestamp < cutoff)
            _samples.Dequeue();
    }
}
