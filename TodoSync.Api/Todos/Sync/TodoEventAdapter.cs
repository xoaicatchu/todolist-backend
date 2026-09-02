using TodoSync.Api.Models;
using TodoSync.Api.Sync.Contracts;

namespace TodoSync.Api.Todos.Sync;

/// <summary>
/// Translates generic sync envelopes at the boundary. It deliberately contains
/// no Todo domain behavior; TodoSyncService remains the single write path.
/// </summary>
public sealed class TodoEventAdapter
{
    public IReadOnlyList<MutationEnvelope> ToMutations(IReadOnlyList<TodoEvent> events) =>
        events.Select(ToMutation).ToArray();

    public IReadOnlyList<TodoEvent> ToEvents(IReadOnlyList<MutationEnvelope> mutations) =>
        mutations.Select(ToEvent).ToArray();

    private static MutationEnvelope ToMutation(TodoEvent evt) => new()
    {
        MutationId = evt.EventId,
        EntityType = "todo",
        EntityId = evt.TodoId,
        MutationType = evt.Type,
        SchemaVersion = 1,
        OccurredAt = evt.CreatedAt,
        Payload = evt.Payload
    };

    private static TodoEvent ToEvent(MutationEnvelope mutation) => new()
    {
        EventId = mutation.MutationId,
        TodoId = mutation.EntityId,
        Type = mutation.MutationType,
        CreatedAt = mutation.OccurredAt,
        Payload = mutation.Payload
    };
}
