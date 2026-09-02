using System.Diagnostics.Metrics;

namespace TodoSync.Api.Sync.Observability;

public sealed class SyncMetrics
{
    public const string MeterName = "TodoSync.Sync";

    private readonly Counter<long> _operations;
    private readonly Histogram<double> _operationDuration;
    private readonly Histogram<double> _pullLag;
    private readonly Counter<long> _changes;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _consumerFailures;
    private readonly Histogram<double> _handlerDuration;

    public SyncMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _operations = meter.CreateCounter<long>("sync_operations_total");
        _operationDuration = meter.CreateHistogram<double>("sync_operation_duration_ms", "ms");
        _pullLag = meter.CreateHistogram<double>("sync_pull_lag_ms", "ms");
        _changes = meter.CreateCounter<long>("sync_changes_total");
        _duplicates = meter.CreateCounter<long>("sync_mutation_duplicates_total");
        _consumerFailures = meter.CreateCounter<long>("sync_consumer_failures_total");
        _handlerDuration = meter.CreateHistogram<double>("sync_handler_duration_ms", "ms");
    }

    public void RecordOperation(string operation, string outcome, TimeSpan duration)
    {
        _operations.Add(1, new("operation", operation), new("outcome", outcome));
        _operationDuration.Record(duration.TotalMilliseconds, new("operation", operation));
    }

    public void RecordPullLag(long lagMs) => _pullLag.Record(Math.Max(0, lagMs));

    public void RecordChange(string entityType, string op) =>
        _changes.Add(1, new("entity_type", entityType), new("op", op));

    public void RecordDuplicate(string entityType, int count = 1)
    {
        if (count > 0)
            _duplicates.Add(count, new("entity_type", entityType));
    }

    public void RecordConsumerFailure(string entityType, string reason) =>
        _consumerFailures.Add(1, new("entity_type", entityType), new("reason", reason));

    public void RecordHandlerDuration(string entityType, TimeSpan duration) =>
        _handlerDuration.Record(duration.TotalMilliseconds, new("entity_type", entityType));
}
