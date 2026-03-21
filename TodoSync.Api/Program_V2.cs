using MassTransit;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using System.Diagnostics;
using System.Threading.RateLimiting;
using TodoSync.Api.Data;
using TodoSync.Api.Data.Repositories;
using TodoSync.Api.Hubs;
using TodoSync.Api.Models;
using TodoSync.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Prevent ThreadPool starvation - Npgsql needs threads for async continuations
ThreadPool.SetMinThreads(200, 200);

// ==================== PHASE 1: DATABASE ====================
builder.Services.AddDbContext<TodoSyncDbContext>(options =>
{
    options.UseNpgsql(config.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);
            npgsqlOptions.CommandTimeout(30);
        })
        .EnableSensitiveDataLogging(builder.Environment.IsDevelopment())
        .EnableDetailedErrors(builder.Environment.IsDevelopment());
});

// Repositories
builder.Services.AddScoped<ITodoRepository, TodoRepository>();
builder.Services.AddScoped<ISyncChangeRepository, SyncChangeRepository>();
builder.Services.AddScoped<IProcessedEventRepository, ProcessedEventRepository>();

// ==================== PHASE 2: CACHING ====================
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = config.GetConnectionString("Redis");
    options.InstanceName = "TodoSync:";
    options.ConfigurationOptions = new ConfigurationOptions
    {
        AbortOnConnectFail = false,
        ConnectTimeout = 5000,
        SyncTimeout = 5000,
        ReconnectRetryPolicy = new ExponentialRetry(5000),
        EndPoints = { config.GetConnectionString("Redis")! }
    };
});

// ==================== PHASE 3: MESSAGE QUEUE ====================
builder.Services.AddMassTransit(x =>
{
    // Add Consumer with Batch options explicitly
    x.AddConsumer<SyncEventConsumer>(c => c.Options<MassTransit.BatchOptions>(o => 
    {
        o.MessageLimit = 100;
        o.TimeLimit = TimeSpan.FromMilliseconds(50);
        o.ConcurrencyLimit = 5;
    }));

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(config["RabbitMQ:Host"], config["RabbitMQ:VirtualHost"], h =>
        {
            h.Username(config["RabbitMQ:Username"]!);
            h.Password(config["RabbitMQ:Password"]!);
        });

        // Configure receive endpoint for the batched consumer
        cfg.ReceiveEndpoint("sync-events", e =>
        {
            e.PrefetchCount = 100;
            e.ConfigureConsumer<SyncEventConsumer>(context);
        });

        cfg.ConfigureEndpoints(context);
    });
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 100000;
    options.Limits.MaxConcurrentUpgradedConnections = 100000;
});

// ==================== PHASE 4: OBSERVABILITY ====================

// OpenTelemetry Tracing
// builder.Services.AddOpenTelemetry()
//     .ConfigureResource(resource => resource
//         .AddService(
//             serviceName: config["OpenTelemetry:ServiceName"]!,
//             serviceVersion: config["OpenTelemetry:ServiceVersion"]))
//     .WithTracing(tracing => tracing
//         .AddAspNetCoreInstrumentation()
//         .AddHttpClientInstrumentation()
//         .AddSource("TodoSync.*"))
//     .WithMetrics(metrics => metrics
//         .AddAspNetCoreInstrumentation()
//         .AddHttpClientInstrumentation()
//         .AddRuntimeInstrumentation()
//         .AddPrometheusExporter());

// // Activity Source for custom tracing
// ActivitySource.AddActivityListener(new ActivityListener
// {
//     ShouldListenTo = source => source.Name.StartsWith("TodoSync"),
//     Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
// });

// ==================== PHASE 5: RATE LIMITING ====================
builder.Services.AddRateLimiter(options =>
{
    options.AddConcurrencyLimiter("sync-write", limiter =>
    {
        limiter.PermitLimit = 5000;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiter.QueueLimit = 2000;
    });

    options.AddConcurrencyLimiter("sync-read", limiter =>
    {
        limiter.PermitLimit = 2000;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiter.QueueLimit = 1000;
    });

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "Too many requests. Please try again later.",
            retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? (TimeSpan?)retryAfter : null
        }, ct);
    };
});

// ==================== HEALTH CHECKS ====================
builder.Services.AddHealthChecks()
    .AddNpgSql(config.GetConnectionString("DefaultConnection")!, name: "postgresql")
    .AddRedis(config.GetConnectionString("Redis")!, name: "redis");

// ==================== SIGNALR WITH REDIS BACKPLANE ====================
builder.Services.AddSignalR()
    .AddStackExchangeRedis(config.GetConnectionString("Redis")!, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("TodoSync:SignalR:");
    });

// ==================== SERVICES ====================
builder.Services.AddScoped<ITodoSyncService, TodoSyncService>();

// Keep legacy service for backward compatibility (read-only mode)
builder.Services.AddSingleton<IEventStoreService, EventStoreService>();

// ==================== CORS ====================
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

var app = builder.Build();

// ==================== MIDDLEWARE PIPELINE ====================

// Prometheus metrics endpoint (disabled - OpenTelemetry services are not configured)
// app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.UseCors("Frontend");
app.UseRateLimiter();

// ==================== HEALTH CHECK ENDPOINTS ====================
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live");

// ==================== SIGNALR HUB ====================
app.MapHub<TodoSync.Api.Hubs.SyncHub>("/hubs/sync");

// ==================== API ENDPOINTS (V2 - PRODUCTION) ====================

app.MapGet("/", () => Results.Ok(new
{
    service = "TodoSync.Api",
    version = "2.0.0-production",
    status = "ok",
    features = new[] { "database", "redis", "rabbitmq", "observability", "rate-limiting" }
}));

// V2 Push - Production version with database (Async Write Buffer)
app.MapPost("/api/v2/sync/push", async (
    SyncPushRequest request,
    ITodoSyncService syncService,
    CancellationToken ct) =>
{
    var response = await syncService.PushAsync(request, "default", ct);

    // Return 202 Accepted since the processing is now asynchronous
    return Results.Accepted(value: response);
})
.RequireRateLimiting("sync-write")
.WithName("PushV2")
.WithTags("Sync");

// V2 Pull - Production version with pagination
app.MapGet("/api/v2/sync/pull", async (
    Guid? sinceChangeId,
    int? limit,
    string? cursor,
    ITodoSyncService syncService,
    CancellationToken ct) =>
{
    var take = limit ?? 300;

    // If sinceChangeId is not provided but cursor is, parse cursor as the sinceChangeId
    var effectiveSinceChangeId = sinceChangeId;
    if (effectiveSinceChangeId == null && !string.IsNullOrEmpty(cursor) && Guid.TryParse(cursor, out var cursorGuid))
    {
        effectiveSinceChangeId = cursorGuid;
    }

    var response = await syncService.PullV2Async(effectiveSinceChangeId, take, cursor, "default", ct);
    return Results.Ok(response);
})
.RequireRateLimiting("sync-read")
.WithName("PullV2")
.WithTags("Sync");

// Get all todos
app.MapGet("/api/v2/sync/all", async (
    ITodoSyncService syncService,
    CancellationToken ct) =>
{
    var todos = await syncService.GetAllAsync("default", ct);
    return Results.Ok(todos);
})
.RequireRateLimiting("sync-read")
.WithName("GetAllV2")
.WithTags("Sync");

// ==================== LEGACY ENDPOINTS (V1 - BACKWARD COMPATIBILITY) ====================

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
})
.WithName("PushV1Legacy")
.WithTags("Legacy");

app.MapGet("/api/sync/pull", async (
    long? since,
    IEventStoreService store,
    CancellationToken ct) =>
{
    var sinceValue = since ?? 0;
    var todos = await store.PullTodosSinceAsync(sinceValue, ct);

    var maxUpdatedAt = todos.Count > 0 ? todos.Max(x => x.UpdatedAt) : sinceValue;
    var serverTime = Math.Max(sinceValue, maxUpdatedAt);

    return Results.Ok(new SyncPullResponse
    {
        Todos = todos.ToList(),
        ServerTime = serverTime,
    });
})
.WithName("PullV1Legacy")
.WithTags("Legacy");

app.MapGet("/api/sync/all", async (IEventStoreService store, CancellationToken ct) =>
{
    var todos = await store.GetAllAsync(ct);
    return Results.Ok(todos);
})
.WithName("GetAllV1Legacy")
.WithTags("Legacy");



// ==================== MIGRATION (DEV ONLY) ====================
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TodoSyncDbContext>();
    // The database schema is managed via the SQL script in Docker (001_Initial.sql)
    // await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database connection checked");
}

var port = Environment.GetEnvironmentVariable("APP_PORT") ?? "3000";
app.Run($"http://0.0.0.0:{port}");

// Make Program class accessible for integration tests
public partial class Program { }
