using FluentAssertions;
using TodoSync.Api.Data.Repositories;
using TodoSync.Api.Models;
using TodoSync.Api.Services;
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Todos.Sync;
using Xunit;

namespace TodoSync.Tests.Sync;

public sealed class TodoMutationHandlerCompatibilityTests
{
    [Fact]
    public async Task HandleBatchAsync_AdaptsEnvelopeAndDelegatesToExistingTodoService()
    {
        var todoService = new RecordingTodoSyncService();
        var handler = new TodoMutationHandler(
            todoService,
            new StubProcessedEventRepository([]),
            new TodoEventAdapter());
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            title = "wrapped",
            priority = "HIGH",
            dayKey = "2026-09-02"
        });
        var mutation = new MutationEnvelope
        {
            MutationId = Guid.NewGuid().ToString(),
            EntityType = "todo",
            EntityId = Guid.NewGuid().ToString(),
            MutationType = "TODO_CREATED",
            OccurredAt = 1234,
            Payload = payload
        };

        var result = await handler.HandleBatchAsync("tenant-a", [mutation]);

        todoService.TenantId.Should().Be("tenant-a");
        todoService.Events.Should().ContainSingle();
        var evt = todoService.Events.Single();
        evt.EventId.Should().Be(mutation.MutationId);
        evt.TodoId.Should().Be(mutation.EntityId);
        evt.Type.Should().Be(mutation.MutationType);
        evt.CreatedAt.Should().Be(mutation.OccurredAt);
        evt.Payload!.Value.GetRawText().Should().Be(payload.GetRawText());
        result.Should().Be(new SyncHandlerBatchResult(1, 0));
    }

    [Fact]
    public async Task HandleBatchAsync_DoesNotReplaceExistingIdempotencyPath()
    {
        var mutationId = Guid.NewGuid().ToString();
        var todoService = new RecordingTodoSyncService();
        var handler = new TodoMutationHandler(
            todoService,
            new StubProcessedEventRepository([mutationId]),
            new TodoEventAdapter());
        var mutation = new MutationEnvelope
        {
            MutationId = mutationId,
            EntityType = "todo",
            EntityId = Guid.NewGuid().ToString(),
            MutationType = "TODO_TOGGLED",
            OccurredAt = 1234
        };

        var result = await handler.HandleBatchAsync("default", [mutation]);

        // The wrapper reports the replay, but still delegates so the existing
        // ProcessedEvent guard remains the source of truth.
        todoService.Events.Should().ContainSingle();
        result.Should().Be(new SyncHandlerBatchResult(0, 1));
    }

    private sealed class RecordingTodoSyncService : ITodoSyncService
    {
        public string? TenantId { get; private set; }
        public IReadOnlyList<TodoEvent> Events { get; private set; } = [];

        public Task<SyncPushResponse> PushAsync(
            SyncPushRequest request,
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.FromResult(new SyncPushResponse());

        public Task ProcessPushBatchAsync(
            string tenantId,
            List<TodoEvent> events,
            CancellationToken ct = default)
        {
            TenantId = tenantId;
            Events = events;
            return Task.CompletedTask;
        }

        public Task<SyncPullResponse> PullAsync(
            long since,
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.FromResult(new SyncPullResponse());

        public Task<TodoSync.Api.Services.SyncPullV2Response> PullV2Async(
            Guid? sinceChangeId,
            int limit,
            string? cursor,
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.FromResult(new TodoSync.Api.Services.SyncPullV2Response());

        public Task<List<TodoItem>> GetAllAsync(
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.FromResult(new List<TodoItem>());
    }

    private sealed class StubProcessedEventRepository(IReadOnlyCollection<string> processedIds)
        : IProcessedEventRepository
    {
        public Task<bool> IsProcessedAsync(string eventId, CancellationToken ct = default) =>
            Task.FromResult(processedIds.Contains(eventId));

        public Task<List<string>> GetProcessedEventIdsAsync(
            IEnumerable<string> eventIds,
            CancellationToken ct = default) =>
            Task.FromResult(eventIds.Where(processedIds.Contains).ToList());

        public Task MarkProcessedAsync(
            string eventId,
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
