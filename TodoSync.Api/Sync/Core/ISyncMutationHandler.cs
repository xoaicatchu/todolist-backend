using TodoSync.Api.Sync.Contracts;

namespace TodoSync.Api.Sync.Core;

public interface ISyncMutationHandler
{
    string EntityType { get; }
    int SchemaVersion { get; }

    SyncValidationResult Validate(MutationEnvelope mutation);

    Task<SyncHandlerBatchResult> HandleBatchAsync(
        string tenantId,
        IReadOnlyList<MutationEnvelope> mutations,
        CancellationToken ct = default);
}
