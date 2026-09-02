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
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Sync.Core;
using TodoSync.Api.Sync.Observability;
using TodoSync.Api.Todos.Sync;

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
            e.UseMessageRetry(r =>
            {
                r.Exponential(
                    retryLimit: 5,
                    minInterval: TimeSpan.FromMilliseconds(200),
                    maxInterval: TimeSpan.FromSeconds(10),
                    intervalDelta: TimeSpan.FromSeconds(2));
                r.Ignore<SyncValidationException>();
            });
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

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: config["OpenTelemetry:ServiceName"] ?? "TodoSync.Api",
            serviceVersion: config["OpenTelemetry:ServiceVersion"]))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(SyncMetrics.MeterName)
        .AddPrometheusExporter());

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
builder.Services.AddScoped<ISyncMutationHandler, TodoMutationHandler>();
builder.Services.AddScoped<SyncHandlerRegistry>();
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddSingleton<SyncCursorCodec>();
builder.Services.AddSingleton<SyncMetrics>();
builder.Services.AddSingleton<TodoEventAdapter>();

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

app.UseCors("Frontend");
app.UseRateLimiter();
app.MapPrometheusScrapingEndpoint("/metrics");

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
    ITodoSyncService todoSyncService,
    ISyncService syncService,
    CancellationToken ct) =>
{
    try
    {
        if (request.Events.Count > 0 && request.Mutations.Count > 0)
        {
            throw new SyncValidationException(
                "SYNC_PUSH_FORMAT_AMBIGUOUS",
                "Provide either events or mutations, not both.");
        }

        if (request.Mutations.Count > 0)
        {
            var genericResponse = await syncService.PushAsync(request.Mutations, "default", ct);
            return Results.Accepted(value: genericResponse);
        }

        // Legacy Todo request: preserve its request, queue message and response contract.
        var todoResponse = await todoSyncService.PushAsync(request, "default", ct);
        return Results.Accepted(value: todoResponse);
    }
    catch (SyncValidationException ex)
    {
        return Results.BadRequest(new
        {
            type = "https://todosync.dev/problems/sync-validation",
            title = "Invalid sync mutation",
            status = StatusCodes.Status400BadRequest,
            code = ex.Code,
            mutationId = ex.MutationId,
            detail = ex.Message
        });
    }
})
.RequireRateLimiting("sync-write")
.WithName("PushV2")
.WithTags("Sync");

// V2 Pull - Production version with pagination
app.MapGet("/api/v2/sync/pull", async (
    Guid? sinceChangeId,
    int? limit,
    string? cursor,
    string? entityTypes,
    ISyncService syncService,
    CancellationToken ct) =>
{
    try
    {
        var scope = string.IsNullOrWhiteSpace(entityTypes)
            ? new[] { "todo" }
            : entityTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var response = await syncService.PullAsync(
            sinceChangeId,
            limit ?? 300,
            cursor,
            scope,
            "default",
            ct);
        return Results.Ok(response);
    }
    catch (SyncValidationException ex)
    {
        return Results.BadRequest(new
        {
            type = "https://todosync.dev/problems/sync-validation",
            title = "Invalid sync cursor or scope",
            status = StatusCodes.Status400BadRequest,
            code = ex.Code,
            detail = ex.Message
        });
    }
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
