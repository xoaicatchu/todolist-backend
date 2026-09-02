using MassTransit;
using Microsoft.AspNetCore.SignalR;
using TodoSync.Api.Hubs;
using TodoSync.Api.Models;
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Sync.Core;
using TodoSync.Api.Sync.Observability;

namespace TodoSync.Api.Services;

/// <summary>
/// Keeps the existing Todo event consumer path and adds a generic message path
/// beside it. Generic Todo messages eventually delegate to the same Todo service.
/// </summary>
public sealed class SyncEventConsumer :
    IConsumer<Batch<SyncPushMessage>>,
    IConsumer<Batch<GenericSyncPushMessage>>
{
    private readonly ITodoSyncService _todoSyncService;
    private readonly ISyncService _syncService;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<SyncEventConsumer> _logger;
    private readonly SyncMetrics _metrics;

    public SyncEventConsumer(
        ITodoSyncService todoSyncService,
        ISyncService syncService,
        IHubContext<SyncHub> hubContext,
        ILogger<SyncEventConsumer> logger,
        SyncMetrics metrics)
    {
        _todoSyncService = todoSyncService;
        _syncService = syncService;
        _hubContext = hubContext;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task Consume(ConsumeContext<Batch<SyncPushMessage>> context)
    {
        var groups = context.Message
            .SelectMany(message => message.Message.Events.Select(evt => new
            {
                message.Message.TenantId,
                Event = evt
            }))
            .GroupBy(x => x.TenantId);

        foreach (var group in groups)
        {
            var events = group.Select(x => x.Event).ToList();
            try
            {
                // This is the original Todo processing path.
                await _todoSyncService.ProcessPushBatchAsync(group.Key, events, context.CancellationToken);
                await _hubContext.Clients.All.SendAsync(
                    "todosChanged",
                    new { changeId = events.Count },
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                _metrics.RecordConsumerFailure("todo", FailureReason(ex));
                _logger.LogError(
                    ex,
                    "Failed to process legacy Todo batch for tenant {TenantId}. Messages will be retried.",
                    group.Key);
                throw;
            }
        }
    }

    public async Task Consume(ConsumeContext<Batch<GenericSyncPushMessage>> context)
    {
        var groups = context.Message
            .SelectMany(message => message.Message.Mutations.Select(mutation => new
            {
                message.Message.TenantId,
                Mutation = mutation
            }))
            .GroupBy(x => x.TenantId);

        foreach (var group in groups)
        {
            var mutations = group.Select(x => x.Mutation).ToList();
            try
            {
                var result = await _syncService.ProcessBatchAsync(
                    group.Key,
                    mutations,
                    context.CancellationToken);

                if (result.AppliedMutations > 0)
                {
                    var notification = new
                    {
                        serverWatermark = result.ServerWatermark,
                        entityTypes = result.EntityTypes
                    };
                    await _hubContext.Clients.All.SendAsync(
                        "changesAvailable",
                        notification,
                        context.CancellationToken);
                    await _hubContext.Clients.All.SendAsync(
                        "todosChanged",
                        notification,
                        context.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                var entityType = mutations.Select(x => x.EntityType).Distinct().Count() == 1
                    ? mutations[0].EntityType
                    : "mixed";
                _metrics.RecordConsumerFailure(entityType, FailureReason(ex));
                _logger.LogError(
                    ex,
                    "Failed to process generic sync batch for tenant {TenantId}. Messages will be retried.",
                    group.Key);
                throw;
            }
        }
    }

    private static string FailureReason(Exception exception) => exception switch
    {
        SyncValidationException => "validation",
        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => "concurrency",
        _ => "transient_or_unknown"
    };
}
