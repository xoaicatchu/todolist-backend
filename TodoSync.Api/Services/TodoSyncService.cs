using Microsoft.Extensions.Caching.Distributed;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TodoSync.Api.Data;
using TodoSync.Api.Data.Entities;
using MassTransit;
using TodoSync.Api.Data.Repositories;
using TodoSync.Api.Models;

namespace TodoSync.Api.Services;

public interface ITodoSyncService
{
    Task<SyncPushResponse> PushAsync(SyncPushRequest request, string tenantId = "default", CancellationToken ct = default);
    Task ProcessPushBatchAsync(string tenantId, List<TodoEvent> events, CancellationToken ct = default);
    Task<SyncPullResponse> PullAsync(long since, string tenantId = "default", CancellationToken ct = default);
    Task<SyncPullV2Response> PullV2Async(Guid? sinceChangeId, int limit, string? cursor, string tenantId = "default", CancellationToken ct = default);
    Task<List<TodoItem>> GetAllAsync(string tenantId = "default", CancellationToken ct = default);
}

public class TodoSyncService : ITodoSyncService
{
    private readonly TodoSyncDbContext _dbContext;
    private readonly ITodoRepository _todoRepo;
    private readonly ISyncChangeRepository _changeRepo;
    private readonly IProcessedEventRepository _processedEventRepo;
    private readonly IDistributedCache _cache;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<TodoSyncService> _logger;
    private static readonly ActivitySource ActivitySource = new("TodoSync.Service");

    public TodoSyncService(
        TodoSyncDbContext dbContext,
        ITodoRepository todoRepo,
        ISyncChangeRepository changeRepo,
        IProcessedEventRepository processedEventRepo,
        IDistributedCache cache,
        IPublishEndpoint publishEndpoint,
        ILogger<TodoSyncService> logger)
    {
        _dbContext = dbContext;
        _todoRepo = todoRepo;
        _changeRepo = changeRepo;
        _processedEventRepo = processedEventRepo;
        _cache = cache;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request, string tenantId = "default", CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("PushEvents");
        activity?.SetTag("tenant_id", tenantId);
        activity?.SetTag("event_count", request.Events.Count);

        if (request.Events.Count > 0)
        {
            await _publishEndpoint.Publish(new SyncPushMessage
            {
                TenantId = tenantId,
                Events = request.Events
            }, ct);
        }

        // Return accepted tracking IDs immediately
        return new SyncPushResponse { AcceptedEventIds = request.Events.Select(e => e.EventId).ToList() };
    }

    public async Task ProcessPushBatchAsync(string tenantId, List<TodoEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;

        using var activity = ActivitySource.StartActivity("ProcessPushBatch");
        activity?.SetTag("tenant_id", tenantId);
        activity?.SetTag("event_count", events.Count);

        var dayKeysToInvalidate = new HashSet<string>();
        
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async (cancellationToken) =>
        {
            dayKeysToInvalidate.Clear();
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            // PHASE 3 OPTIMIZATION: Bulk FETCH all required data upfront to eliminate N+1 SELECTs
            var eventIds = events.Select(e => e.EventId).Distinct().ToList();
            var processedEventIds = await _processedEventRepo.GetProcessedEventIdsAsync(eventIds, ct);
            var processedSet = new HashSet<string>(processedEventIds);

            var todoIds = events.Select(e => e.TodoId).Distinct().ToList();
            var existingTodosList = await _todoRepo.GetByIdsAsync(todoIds, tenantId, ct);
            var existingTodosDict = existingTodosList.ToDictionary(t => t.Id);

            foreach (var evt in events.OrderBy(e => e.CreatedAt))
            {
                // Idempotency check (Memory lookup)
                if (processedSet.Contains(evt.EventId))
                {
                    _logger.LogDebug("Event {EventId} already processed (idempotent)", evt.EventId);
                    continue;
                }

                existingTodosDict.TryGetValue(evt.TodoId, out var existingTodo);

                // Apply event to domain
                var todo = await ApplyEventAsync(evt, existingTodo, tenantId, ct);

                // TODO_REORDERED affects multiple todos — handle separately
                // ReorderTodosAsync returns null; we use GetByIdsAsync (tracked, NOT AsNoTracking)
                // to get the updated entities from the change tracker with correct SortOrder
                if (todo == null && evt.Type == "TODO_REORDERED")
                {
                    var dayKey = GetPayloadString(evt.Payload, "dayKey");
                    var orderedIds = GetPayloadStringArray(evt.Payload, "orderedIds");

                    if (dayKey != null && orderedIds != null)
                    {
                        // GetByIdsAsync does NOT use AsNoTracking — returns change-tracked entities
                        // with the UPDATED SortOrder from ReorderTodosAsync
                        var updatedTodos = await _todoRepo.GetByIdsAsync(orderedIds, tenantId, ct);
                        foreach (var reorderedTodo in updatedTodos)
                        {
                            var reorderChange = new SyncChangeEntity
                            {
                                TenantId = tenantId,
                                EntityType = "todo",
                                EntityId = reorderedTodo.Id,
                                Operation = "upsert",
                                PayloadJson = JsonSerializer.Serialize(MapToModel(reorderedTodo)),
                                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                TraceId = Activity.Current?.TraceId.ToString(),
                                EventId = evt.EventId
                            };
                            await _changeRepo.CreateAsync(reorderChange, ct);
                            dayKeysToInvalidate.Add(reorderedTodo.DayKey);
                        }
                    }

                    // Mark reorder event as processed
                    await _processedEventRepo.MarkProcessedAsync(evt.EventId, tenantId, ct);
                    processedSet.Add(evt.EventId);
                    continue;
                }

                if (todo == null)
                {
                    _logger.LogWarning("Failed to apply event {EventId}", evt.EventId);
                    continue;
                }

                // If it's a new Todo, add it to the tracking dictionary for subsequent events in the same batch
                if (existingTodo == null)
                {
                    existingTodosDict[todo.Id] = todo;
                }

                // Record sync change
                var change = new SyncChangeEntity
                {
                    TenantId = tenantId,
                    EntityType = "todo",
                    EntityId = evt.TodoId,
                    Operation = evt.Type == "TODO_DELETED" ? "delete" : "upsert",
                    PayloadJson = JsonSerializer.Serialize(MapToModel(todo)),
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    TraceId = Activity.Current?.TraceId.ToString(),
                    EventId = evt.EventId
                };
                await _changeRepo.CreateAsync(change, ct);

                // Mark as processed
                await _processedEventRepo.MarkProcessedAsync(evt.EventId, tenantId, ct);
                processedSet.Add(evt.EventId); // Update local cache

                dayKeysToInvalidate.Add(todo.DayKey);
            }

            // Execute a single batched database round-trip then commit
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, ct);

        // Invalidate cache after commit
        await _cache.RemoveAsync($"todos:{tenantId}", ct);
        foreach (var key in dayKeysToInvalidate)
        {
            await _cache.RemoveAsync($"todos:{tenantId}:{key}", ct);
        }

        _logger.LogInformation("Processed {Count} events for tenant {TenantId}", events.Count, tenantId);
    }

    public async Task<SyncPullResponse> PullAsync(long since, string tenantId = "default", CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("PullTodos");
        activity?.SetTag("tenant_id", tenantId);
        activity?.SetTag("since", since);

        // Try cache first
        var cacheKey = $"todos:pull:{tenantId}:{since}";
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached != null)
        {
            _logger.LogDebug("Cache hit for pull since {Since}", since);
            return JsonSerializer.Deserialize<SyncPullResponse>(cached)!;
        }

        var todos = await _todoRepo.GetUpdatedSinceAsync(since, tenantId, ct);
        var models = todos.Select(MapToModel).ToList();

        var serverTime = todos.Count > 0 ? todos.Max(t => t.UpdatedAt) : since;

        var response = new SyncPullResponse
        {
            Todos = models,
            ServerTime = serverTime
        };

        // Cache for 5 seconds to provide eventual consistency without stale data
        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5)
        }, ct);

        return response;
    }

    public async Task<SyncPullV2Response> PullV2Async(
        Guid? sinceChangeId,
        int limit,
        string? cursor,
        string tenantId = "default",
        CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("PullV2");
        activity?.SetTag("tenant_id", tenantId);
        activity?.SetTag("since_change_id", sinceChangeId?.ToString());
        activity?.SetTag("limit", limit);

        var take = Math.Clamp(limit, 1, 500);
        var changes = await _changeRepo.GetChangesSinceAsync(sinceChangeId, take, tenantId, ct);

        // Deduplicate: keep only the latest change per entity to avoid replaying
        // intermediate states (e.g., create → toggle → delete) which causes UI flicker
        var deduped = changes
            .GroupBy(c => c.EntityId)
            .Select(g => g.OrderByDescending(c => c.ChangeId).First())
            .OrderBy(c => c.ChangeId)
            .ToList();

        var changeModels = deduped.Select(c => new ChangeEnvelope
        {
            ChangeId = c.ChangeId,
            EntityType = c.EntityType,
            EntityId = c.EntityId,
            Op = c.Operation,
            Payload = JsonSerializer.Deserialize<TodoItem>(c.PayloadJson)
        }).ToList();

        // Use the LAST raw change (not deduped) for cursor so pagination doesn't skip records
        var hasMore = changes.Count == take;
        var serverWatermark = await _changeRepo.GetLatestChangeIdAsync(tenantId, ct);

        return new SyncPullV2Response
        {
            Changes = changeModels,
            ServerWatermark = serverWatermark,
            NextCursor = hasMore ? changes.Last().ChangeId.ToString() : null,
            HasMore = hasMore
        };
    }

    public async Task<List<TodoItem>> GetAllAsync(string tenantId = "default", CancellationToken ct = default)
    {
        // Try cache first
        var cacheKey = $"todos:{tenantId}";
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached != null)
        {
            return JsonSerializer.Deserialize<List<TodoItem>>(cached)!;
        }

        var todos = await _todoRepo.GetAllAsync(tenantId, ct);
        var models = todos.Select(MapToModel).ToList();

        // Cache for 1 minute
        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(models), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
        }, ct);

        return models;
    }

    private async Task<TodoEntity?> ApplyEventAsync(TodoEvent evt, TodoEntity? existing, string tenantId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return evt.Type switch
        {
            "TODO_CREATED" => await CreateTodoAsync(evt, tenantId, now, ct),
            "TODO_TOGGLED" => await ToggleTodoAsync(existing, now, ct),
            "TODO_RENAMED" => await RenameTodoAsync(existing, evt, now, ct),
            "TODO_REORDERED" => await ReorderTodosAsync(evt, tenantId, now, ct),
            "TODO_DELETED" => await DeleteTodoAsync(existing, now, ct),
            "TODO_UPSERTED_FROM_SERVER" => await UpsertFromServerAsync(evt, existing, ct),
            _ => null
        };
    }

    private async Task<TodoEntity> CreateTodoAsync(TodoEvent evt, string tenantId, long now, CancellationToken ct)
    {
        var title = GetPayloadString(evt.Payload, "title") ?? "";
        var priority = GetPayloadString(evt.Payload, "priority") ?? "MEDIUM";
        var dayKey = GetPayloadString(evt.Payload, "dayKey") ?? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var existingTodos = await _todoRepo.GetByDayKeyAsync(dayKey, tenantId, ct);
        var maxOrder = existingTodos.Count > 0 ? existingTodos.Max(t => t.SortOrder) : 0;

        var todo = new TodoEntity
        {
            Id = evt.TodoId,
            TenantId = tenantId,
            Title = title,
            Priority = priority,
            DayKey = dayKey,
            SortOrder = maxOrder + 1,
            Completed = false,
            Deleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        return await _todoRepo.CreateAsync(todo, ct);
    }

    private async Task<TodoEntity?> ToggleTodoAsync(TodoEntity? existing, long now, CancellationToken ct)
    {
        if (existing == null) return null;
        existing.Completed = !existing.Completed;
        existing.UpdatedAt = now;
        existing.Version++;
        return await _todoRepo.UpdateAsync(existing, ct);
    }

    private async Task<TodoEntity?> RenameTodoAsync(TodoEntity? existing, TodoEvent evt, long now, CancellationToken ct)
    {
        if (existing == null) return null;
        existing.Title = GetPayloadString(evt.Payload, "title") ?? existing.Title;
        existing.Priority = GetPayloadString(evt.Payload, "priority") ?? existing.Priority;
        existing.UpdatedAt = now;
        existing.Version++;
        return await _todoRepo.UpdateAsync(existing, ct);
    }

    private async Task<TodoEntity?> ReorderTodosAsync(TodoEvent evt, string tenantId, long now, CancellationToken ct)
    {
        var dayKey = GetPayloadString(evt.Payload, "dayKey");
        var orderedIds = GetPayloadStringArray(evt.Payload, "orderedIds");

        if (string.IsNullOrWhiteSpace(dayKey) || orderedIds == null || orderedIds.Count == 0) return null;

        // PHASE 2 OPTIMIZATION: Eliminate N+1 queries by bulk fetching all affected Todos
        var existingTodos = await _todoRepo.GetByIdsAsync(orderedIds, tenantId, ct);
        var todoDict = existingTodos.ToDictionary(t => t.Id);

        for (int i = 0; i < orderedIds.Count; i++)
        {
            if (todoDict.TryGetValue(orderedIds[i], out var todo) && todo.DayKey == dayKey)
            {
                todo.SortOrder = i + 1;
                todo.UpdatedAt = now;
                todo.Version++;
                await _todoRepo.UpdateAsync(todo, ct);
            }
        }

        return null; // No single todo to return
    }

    private async Task<TodoEntity?> DeleteTodoAsync(TodoEntity? existing, long now, CancellationToken ct)
    {
        if (existing == null) return null;
        existing.Deleted = true;
        existing.UpdatedAt = now;
        existing.Version++;
        return await _todoRepo.UpdateAsync(existing, ct);
    }

    private async Task<TodoEntity?> UpsertFromServerAsync(TodoEvent evt, TodoEntity? existing, CancellationToken ct)
    {
        if (evt.Payload == null) return null;

        var incoming = JsonSerializer.Deserialize<TodoItem>(evt.Payload.Value.GetRawText());
        if (incoming == null) return null;

        if (existing == null || incoming.UpdatedAt >= existing.UpdatedAt)
        {
            var entity = MapToEntity(incoming);
            return existing == null
                ? await _todoRepo.CreateAsync(entity, ct)
                : await _todoRepo.UpdateAsync(entity, ct);
        }

        return existing;
    }

    private static string? GetPayloadString(JsonElement? payload, string property)
    {
        if (payload == null || payload.Value.ValueKind != JsonValueKind.Object) return null;
        return payload.Value.TryGetProperty(property, out var p) ? p.GetString() : null;
    }

    private static List<string>? GetPayloadStringArray(JsonElement? payload, string property)
    {
        if (payload == null || payload.Value.ValueKind != JsonValueKind.Object) return null;
        if (!payload.Value.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.Array) return null;

        return p.EnumerateArray()
            .Select(x => x.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    private static TodoItem MapToModel(TodoEntity entity) => new()
    {
        Id = entity.Id,
        Title = entity.Title,
        Priority = entity.Priority,
        DayKey = entity.DayKey,
        SortOrder = entity.SortOrder,
        Completed = entity.Completed,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        Deleted = entity.Deleted
    };

    private static TodoEntity MapToEntity(TodoItem model) => new()
    {
        Id = model.Id,
        TenantId = "default",
        Title = model.Title,
        Priority = model.Priority,
        DayKey = model.DayKey,
        SortOrder = model.SortOrder,
        Completed = model.Completed,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        Deleted = model.Deleted,
        Version = 1
    };
}

public class ChangeEnvelope
{
    public Guid ChangeId { get; set; }
    public string EntityType { get; set; } = default!;
    public string EntityId { get; set; } = default!;
    public string Op { get; set; } = default!;
    public TodoItem? Payload { get; set; }
}

public class SyncPullV2Response
{
    public List<ChangeEnvelope> Changes { get; set; } = [];
    public Guid? ServerWatermark { get; set; }
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}
