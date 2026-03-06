using Microsoft.AspNetCore.SignalR;
using TodoSync.Api.Hubs;
using TodoSync.Api.Models;
using TodoSync.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins("http://localhost:4300", "http://localhost:4200")
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

    // notify realtime clients to pull latest changes
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
    var serverTime = await store.GetServerTimeAsync(ct);
    return Results.Ok(new SyncPullResponse
    {
        Todos = todos.ToList(),
        ServerTime = serverTime,
    });
});

app.MapGet("/api/sync/all", async (IEventStoreService store, CancellationToken ct) =>
{
    var todos = await store.GetAllAsync(ct);
    return Results.Ok(todos);
});

app.MapHub<SyncHub>("/hubs/sync");

app.Run("http://localhost:3000");
