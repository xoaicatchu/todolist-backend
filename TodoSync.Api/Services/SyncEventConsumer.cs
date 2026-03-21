using MassTransit;
using Microsoft.AspNetCore.SignalR;
using TodoSync.Api.Hubs;
using TodoSync.Api.Models;

namespace TodoSync.Api.Services;

/// <summary>
/// Background consumer that processes batches of incoming sync events from RabbitMQ.
/// This acts as an async buffer to protect PostgreSQL from concurrent write saturation.
/// </summary>
public class SyncEventConsumer : IConsumer<Batch<SyncPushMessage>>
{
    private readonly ITodoSyncService _syncService;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<SyncEventConsumer> _logger;

    public SyncEventConsumer(
        ITodoSyncService syncService, 
        IHubContext<SyncHub> hubContext,
        ILogger<SyncEventConsumer> logger)
    {
        _syncService = syncService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<Batch<SyncPushMessage>> context)
    {
        _logger.LogInformation("Received batch of {Count} push messages from message broker", context.Message.Length);

        // Group messages by tenant ID to avoid cross-tenant database transactions
        var groups = context.Message
            .SelectMany(m => m.Message.Events.Select(e => new { m.Message.TenantId, Event = e }))
            .GroupBy(x => x.TenantId);

        foreach (var group in groups)
        {
            var tenantId = group.Key;
            var tenantEvents = group.Select(x => x.Event).ToList();

            try
            {
                // Process the entire flattened list of events for this tenant in a single DB transaction
                await _syncService.ProcessPushBatchAsync(tenantId, tenantEvents, context.CancellationToken);
                
                // Notify connected clients via SignalR that updates are available
                await _hubContext.Clients.All.SendAsync("todosChanged", new { changeId = tenantEvents.Count }, context.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process push batch for tenant {TenantId}. Messages will be retried.", tenantId);
                throw; // Rethrow lets MassTransit handle retries and moving to poison/DLQ
            }
        }
    }
}
