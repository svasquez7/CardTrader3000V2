using System.Collections.Concurrent;
using System.Threading.Channels;
using CardTrader3000.Data.Entities;

namespace CardTrader3000.Services.Import;

/// <summary>
/// FIFO of batch ids waiting to be processed. A single worker drains it, so batches never run
/// concurrently (which keeps the inventory merge free of duplicate-key races on SQLite).
/// </summary>
public sealed class ImportQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(int batchId, CancellationToken ct = default) => _channel.Writer.WriteAsync(batchId, ct);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed record ImportProgress(
    int BatchId,
    ImportBatchStatus Status,
    int CardsCompleted,
    int CardsTotal,
    string Message)
{
    public bool IsFinished => Status is ImportBatchStatus.Completed or ImportBatchStatus.CompletedWithErrors or ImportBatchStatus.Failed;
    public int Percent => CardsTotal == 0 ? 0 : (int)Math.Round(CardsCompleted * 100.0 / CardsTotal);
}

/// <summary>
/// In-memory live status for running batches. UI components subscribe to <see cref="Changed"/>;
/// the database remains the source of truth once a batch finishes.
/// </summary>
public sealed class ImportProgressTracker
{
    private readonly ConcurrentDictionary<int, ImportProgress> _progress = new();

    /// <summary>Raised on a background thread. Handlers in components must use InvokeAsync.</summary>
    public event Action<ImportProgress>? Changed;

    public ImportProgress? Get(int batchId) => _progress.TryGetValue(batchId, out var p) ? p : null;

    public void Report(ImportProgress progress)
    {
        _progress[progress.BatchId] = progress;
        Changed?.Invoke(progress);
    }
}
