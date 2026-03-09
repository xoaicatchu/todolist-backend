using Microsoft.AspNetCore.SignalR;
using TodoSync.Api.Hubs;
using TodoSync.Api.Models;
using TodoSync.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .SetIsOriginAllowed(origin =>
            origin.StartsWith("http://localhost:") ||
            origin.StartsWith("https://") ||
            origin.StartsWith("http://"))
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<IEventStoreService, EventStoreService>();

var app = builder.Build();

app.UseCors("Frontend");

app.MapGet("/", () => Results.Ok(new { service = "TodoSync.Api", status = "ok" }));

app.MapPost("/api/sync/push", async (
    SyncPushRequest request,
    IEventStoreService store,
    IHubContext<SyncHub> hub,
    CancellationToken ct) =>
{
    var accepted = await store.AppendEventsAsync(request.Events, ct);
    var serverTime = await store.GetServerTimeAsync(ct);

    await hub.Clients.All.SendAsync("todosChanged", new { serverTime }, ct);

    return Results.Ok(new SyncPushResponse { AcceptedEventIds = accepted.ToList() });
});

app.MapGet("/api/sync/pull", async (
    long? since,
    IEventStoreService store,
    CancellationToken ct) =>
{
    var sinceValue = since ?? 0;
    var todos = await store.PullTodosSinceAsync(sinceValue, ct);

    // Watermark should follow persisted todo update timestamps, not wall-clock now,
    // to avoid skipping updates (e.g. reorder) between pulls.
    var maxUpdatedAt = todos.Count > 0 ? todos.Max(x => x.UpdatedAt) : sinceValue;
    var serverTime = Math.Max(sinceValue, maxUpdatedAt);

    return Results.Ok(new SyncPullResponse
    {
        Todos = todos.ToList(),
        ServerTime = serverTime,
    });
});


app.MapGet("/api/sync/v2/pull", async (
    long? sinceChangeId,
    int? limit,
    string? cursor,
    IEventStoreService store,
    CancellationToken ct) =>
{
    var sinceValue = sinceChangeId ?? 0;
    var take = Math.Clamp(limit ?? 300, 1, 500);

    var todos = (await store.PullTodosSinceAsync(sinceValue, ct)).ToList();

    var offset = 0;
    if (!string.IsNullOrWhiteSpace(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0)
    {
        offset = parsed;
    }

    var page = todos.Skip(offset).Take(take).ToList();
    var nextOffset = offset + page.Count;
    var hasMore = nextOffset < todos.Count;

    var changes = page.Select(t => new
    {
        changeId = t.UpdatedAt,
        entityType = "todo",
        entityId = t.Id,
        op = t.Deleted ? "delete" : "upsert",
        payload = t,
    }).ToList();

    var serverWatermark = todos.Count > 0 ? Math.Max(sinceValue, todos.Max(x => x.UpdatedAt)) : sinceValue;

    return Results.Ok(new
    {
        changes,
        serverWatermark,
        nextCursor = hasMore ? nextOffset.ToString() : null,
        hasMore,
    });
});
app.MapGet("/api/sync/all", async (IEventStoreService store, CancellationToken ct) =>
{
    var todos = await store.GetAllAsync(ct);
    return Results.Ok(todos);
});

app.MapHub<SyncHub>("/hubs/sync");

app.Run("http://localhost:3000");



