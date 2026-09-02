using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using TodoSync.Api.Data.Entities;
using TodoSync.Api.Data.Repositories;
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Sync.Observability;

namespace TodoSync.Api.Sync.Core;

public interface ISyncService
{
    Task<GenericSyncPushResponse> PushAsync(
        IReadOnlyList<MutationEnvelope> mutations,
        string tenantId = "default",
        CancellationToken ct = default);

    Task<SyncBatchResult> ProcessBatchAsync(
        string tenantId,
        IReadOnlyList<MutationEnvelope> mutations,
        CancellationToken ct = default);

    Task<SyncPullV2Response> PullAsync(
        Guid? sinceChangeId,
        int limit,
        string? cursor,
        IEnumerable<string>? entityTypes,
        string tenantId = "default",
        CancellationToken ct = default);
}

/// <summary>
/// Generic orchestration only. Entity handlers own domain behavior and transaction
/// boundaries; the Todo handler delegates those responsibilities to TodoSyncService.
/// </summary>
public sealed class SyncService : ISyncService
{
    private readonly ISyncChangeRepository _changeRepository;
    private readonly SyncHandlerRegistry _handlerRegistry;
    private readonly SyncCursorCodec _cursorCodec;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly SyncMetrics _metrics;

    public SyncService(
        ISyncChangeRepository changeRepository,
        SyncHandlerRegistry handlerRegistry,
        SyncCursorCodec cursorCodec,
        IPublishEndpoint publishEndpoint,
        SyncMetrics metrics)
    {
        _changeRepository = changeRepository;
        _handlerRegistry = handlerRegistry;
        _cursorCodec = cursorCodec;
        _publishEndpoint = publishEndpoint;
        _metrics = metrics;
    }

    public async Task<GenericSyncPushResponse> PushAsync(
        IReadOnlyList<MutationEnvelope> mutations,
        string tenantId = "default",
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "success";

        try
        {
            ValidateBatch(mutations);

            await _publishEndpoint.Publish(new GenericSyncPushMessage
            {
                TenantId = tenantId,
                Mutations = mutations.ToList()
            }, ct);

            return new GenericSyncPushResponse
            {
                AcceptedMutationIds = mutations.Select(x => x.MutationId).ToList()
            };
        }
        catch
        {
            outcome = "error";
            throw;
        }
        finally
        {
            _metrics.RecordOperation("push", outcome, stopwatch.Elapsed);
        }
    }

    public async Task<SyncBatchResult> ProcessBatchAsync(
        string tenantId,
        IReadOnlyList<MutationEnvelope> mutations,
        CancellationToken ct = default)
    {
        if (mutations.Count == 0)
            return new SyncBatchResult(null, [], 0, 0);

        ValidateBatch(mutations);

        var applied = 0;
        var duplicates = 0;
        var handledTypes = new HashSet<string>(StringComparer.Ordinal);

        var groups = mutations
            .GroupBy(x => (EntityType: SyncHandlerRegistry.Normalize(x.EntityType), x.SchemaVersion))
            .OrderBy(x => x.Key.EntityType, StringComparer.Ordinal)
            .ThenBy(x => x.Key.SchemaVersion);

        foreach (var group in groups)
        {
            var handler = _handlerRegistry.Resolve(group.Key.EntityType, group.Key.SchemaVersion);
            var batch = group.OrderBy(x => x.OccurredAt).ThenBy(x => x.MutationId, StringComparer.Ordinal).ToArray();
            var stopwatch = Stopwatch.StartNew();
            SyncHandlerBatchResult result;

            try
            {
                result = await handler.HandleBatchAsync(tenantId, batch, ct);
            }
            finally
            {
                _metrics.RecordHandlerDuration(group.Key.EntityType, stopwatch.Elapsed);
            }

            applied += result.AppliedMutations;
            duplicates += result.DuplicateMutations;
            handledTypes.Add(group.Key.EntityType);
            _metrics.RecordDuplicate(group.Key.EntityType, result.DuplicateMutations);
        }

        var watermark = handledTypes.Count == 0
            ? null
            : await _changeRepository.GetLatestPositionAsync(handledTypes, tenantId, ct);

        return new SyncBatchResult(
            watermark?.ChangeId,
            handledTypes.ToArray(),
            applied,
            duplicates);
    }

    public async Task<SyncPullV2Response> PullAsync(
        Guid? sinceChangeId,
        int limit,
        string? cursor,
        IEnumerable<string>? entityTypes,
        string tenantId = "default",
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "success";

        try
        {
            var scope = _handlerRegistry.NormalizeAndValidateScope(entityTypes);
            var take = Math.Clamp(limit, 1, 500);

            long afterSequence;
            SyncChangePosition? upperBound;

            // Cursor deliberately wins when legacy clients send both parameters.
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                var decoded = _cursorCodec.Decode(cursor, tenantId, scope);
                afterSequence = decoded.AfterSequence;
                upperBound = new SyncChangePosition(decoded.UpperBoundSequence, decoded.UpperBoundChangeId);
            }
            else
            {
                afterSequence = 0;
                if (sinceChangeId is { } checkpoint && checkpoint != Guid.Empty)
                {
                    afterSequence = await _changeRepository.GetSequenceAsync(checkpoint, scope, tenantId, ct)
                        ?? throw new SyncValidationException(
                            "SYNC_CHECKPOINT_UNKNOWN",
                            "The supplied checkpoint is not present in this sync scope.");
                }

                upperBound = await _changeRepository.GetLatestPositionAsync(scope, tenantId, ct);
            }

            if (upperBound is null)
            {
                return new SyncPullV2Response
                {
                    ServerWatermark = sinceChangeId
                };
            }

            var window = await _changeRepository.GetChangesWindowAsync(
                afterSequence,
                upperBound.Sequence,
                take + 1,
                scope,
                tenantId,
                ct);

            var hasMore = window.Count > take;
            var page = window.Take(take).ToList();
            var nextCursor = hasMore && page.Count > 0
                ? _cursorCodec.Encode(
                    tenantId,
                    scope,
                    page[^1].ChangeSequence,
                    upperBound.Sequence,
                    upperBound.ChangeId)
                : null;

            if (page.Count > 0)
            {
                var lag = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - page.Min(x => x.CreatedAt);
                _metrics.RecordPullLag(lag);
            }

            return new SyncPullV2Response
            {
                Changes = page.Select(ToEnvelope).ToList(),
                ServerWatermark = upperBound.ChangeId,
                NextCursor = nextCursor,
                HasMore = hasMore
            };
        }
        catch
        {
            outcome = "error";
            throw;
        }
        finally
        {
            _metrics.RecordOperation("pull", outcome, stopwatch.Elapsed);
        }
    }

    private void ValidateBatch(IReadOnlyList<MutationEnvelope> mutations)
    {
        if (mutations.Count == 0 || mutations.Count > 500)
            throw new SyncValidationException(
                "SYNC_BATCH_INVALID",
                "A push batch must contain 1 to 500 mutations.");

        foreach (var mutation in mutations)
        {
            mutation.EntityType = SyncHandlerRegistry.Normalize(mutation.EntityType);
            var handler = _handlerRegistry.Resolve(mutation.EntityType, mutation.SchemaVersion);
            var validation = handler.Validate(mutation);
            if (!validation.IsValid)
            {
                throw new SyncValidationException(
                    validation.Code ?? "SYNC_MUTATION_INVALID",
                    validation.Message ?? "The mutation is invalid.",
                    mutation.MutationId);
            }
        }
    }

    private static ChangeEnvelope ToEnvelope(SyncChangeEntity entity) => new()
    {
        ChangeId = entity.ChangeId,
        EntityType = entity.EntityType,
        EntityId = entity.EntityId,
        Op = entity.Operation,
        SchemaVersion = 1,
        OccurredAt = entity.CreatedAt,
        SourceMutationId = entity.EventId,
        Payload = JsonSerializer.Deserialize<JsonElement>(entity.PayloadJson)
    };
}
