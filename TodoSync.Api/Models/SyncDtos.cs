namespace TodoSync.Api.Models;

using TodoSync.Api.Sync.Contracts;

public sealed class SyncPushRequest
{
    public List<TodoEvent> Events { get; set; } = [];
    public List<MutationEnvelope> Mutations { get; set; } = [];
}

public sealed class SyncPushResponse
{
    public List<string> AcceptedEventIds { get; set; } = [];
}

public sealed class SyncPullResponse
{
    public List<TodoItem> Todos { get; set; } = [];
    public long ServerTime { get; set; }
}

public sealed class SyncPushMessage
{
    public string TenantId { get; set; } = "default";
    public List<TodoEvent> Events { get; set; } = [];
}
