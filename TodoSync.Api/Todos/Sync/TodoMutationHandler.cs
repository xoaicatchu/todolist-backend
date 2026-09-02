using System.Text.Json;
using TodoSync.Api.Data.Repositories;
using TodoSync.Api.Services;
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Sync.Core;

namespace TodoSync.Api.Todos.Sync;

/// <summary>
/// Generic handler adapter for Todo. All writes, change-log entries, cache
/// invalidation and idempotency still run through the existing TodoSyncService.
/// </summary>
public sealed class TodoMutationHandler : ISyncMutationHandler
{
    private static readonly HashSet<string> SupportedMutations = new(StringComparer.Ordinal)
    {
        "TODO_CREATED",
        "TODO_TOGGLED",
        "TODO_RENAMED",
        "TODO_REORDERED",
        "TODO_DELETED",
        "TODO_UPSERTED_FROM_SERVER"
    };

    private readonly ITodoSyncService _todoSyncService;
    private readonly IProcessedEventRepository _processedEventRepository;
    private readonly TodoEventAdapter _adapter;

    public TodoMutationHandler(
        ITodoSyncService todoSyncService,
        IProcessedEventRepository processedEventRepository,
        TodoEventAdapter adapter)
    {
        _todoSyncService = todoSyncService;
        _processedEventRepository = processedEventRepository;
        _adapter = adapter;
    }

    public string EntityType => "todo";
    public int SchemaVersion => 1;

    public SyncValidationResult Validate(MutationEnvelope mutation)
    {
        if (string.IsNullOrWhiteSpace(mutation.MutationId) || mutation.MutationId.Length > 36)
            return SyncValidationResult.Failure(
                "SYNC_MUTATION_ID_INVALID",
                "mutationId is required and must be at most 36 characters.");

        if (string.IsNullOrWhiteSpace(mutation.EntityId) || mutation.EntityId.Length > 36)
            return SyncValidationResult.Failure(
                "SYNC_ENTITY_ID_INVALID",
                "entityId is required and must be at most 36 characters.");

        if (!SupportedMutations.Contains(mutation.MutationType))
            return SyncValidationResult.Failure(
                "TODO_MUTATION_UNSUPPORTED",
                $"Unsupported Todo mutation '{mutation.MutationType}'.");

        if (mutation.MutationType == "TODO_REORDERED" &&
            (!HasString(mutation.Payload, "dayKey") || !HasNonEmptyArray(mutation.Payload, "orderedIds")))
        {
            return SyncValidationResult.Failure(
                "TODO_REORDER_INVALID",
                "TODO_REORDERED requires dayKey and orderedIds.");
        }

        return SyncValidationResult.Success;
    }

    public async Task<SyncHandlerBatchResult> HandleBatchAsync(
        string tenantId,
        IReadOnlyList<MutationEnvelope> mutations,
        CancellationToken ct = default)
    {
        var events = _adapter.ToEvents(mutations);
        var uniqueIds = events.Select(x => x.EventId).Distinct(StringComparer.Ordinal).ToArray();
        var alreadyProcessed = await _processedEventRepository.GetProcessedEventIdsAsync(uniqueIds, ct);
        var applied = uniqueIds.Length - alreadyProcessed.Distinct(StringComparer.Ordinal).Count();
        var duplicates = events.Count - applied;

        await _todoSyncService.ProcessPushBatchAsync(tenantId, events.ToList(), ct);

        return new SyncHandlerBatchResult(applied, duplicates);
    }

    private static bool HasString(JsonElement? payload, string property)
    {
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
            return false;

        return payload.Value.TryGetProperty(property, out var item) &&
               item.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(item.GetString());
    }

    private static bool HasNonEmptyArray(JsonElement? payload, string property)
    {
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
            return false;

        return payload.Value.TryGetProperty(property, out var item) &&
               item.ValueKind == JsonValueKind.Array &&
               item.GetArrayLength() > 0;
    }
}
