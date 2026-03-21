using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TodoSync.Api.Data;
using TodoSync.Api.Services;

namespace TodoSync.Tests.Integration;

/// <summary>
/// Custom WebApplicationFactory that uses real infrastructure (PostgreSQL, Redis, RabbitMQ)
/// running via Docker Compose, with an isolated test database per test fixture.
/// </summary>
public class TodoSyncWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _testDbName = $"todosync_test_{Guid.NewGuid():N}";
    private readonly string _testConnectionString;

    public TodoSyncWebApplicationFactory()
    {
        _testConnectionString = $"Host=localhost;Port=5433;Database={_testDbName};Username=postgres;Password=postgres;Timeout=30;CommandTimeout=30;Pooling=true;";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Override connection string via configuration so that the original 
        // UseNpgsql call in Program_V2.cs picks up the test database
        builder.UseSetting("ConnectionStrings:DefaultConnection", _testConnectionString);

        builder.ConfigureServices(services =>
        {
            // ===== 1. Use MassTransit Test Harness to replace RabbitMQ with in-memory =====
            services.AddMassTransitTestHarness(x =>
            {
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.ReceiveEndpoint("sync-events-test", e =>
                    {
                        e.ConfigureConsumer<TodoSync.Api.Services.SyncEventConsumer>(context);
                    });
                    cfg.ConfigureEndpoints(context);
                });
            });

            // ===== 2. Replace SignalR Redis backplane with in-memory =====
            var signalRRedisDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("StackExchange") == true ||
                            d.ImplementationType?.FullName?.Contains("StackExchange") == true ||
                            d.ImplementationType?.FullName?.Contains("RedisHubLifetimeManager") == true)
                .ToList();
            foreach (var d in signalRRedisDescriptors)
                services.Remove(d);

            var hubLifetimeManagers = services
                .Where(d => d.ServiceType.IsGenericType &&
                            d.ServiceType.GetGenericTypeDefinition() == typeof(HubLifetimeManager<>))
                .ToList();
            foreach (var d in hubLifetimeManagers)
                services.Remove(d);

            services.AddSignalR();

            // ===== 3. Replace Redis cache with in-memory cache =====
            // (StackExchange removal above also removes IDistributedCache)
            services.AddDistributedMemoryCache();

            // ===== 3. Clear infrastructure health checks =====
            services.PostConfigure<HealthCheckServiceOptions>(opts => opts.Registrations.Clear());


        });
    }

    /// <summary>
    /// Ensures the test database is created with the correct schema.
    /// </summary>
    public void EnsureDatabaseCreated()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoSyncDbContext>();
        db.Database.EnsureCreated();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                using var scope = Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TodoSyncDbContext>();
                db.Database.EnsureDeleted();
            }
            catch { /* ignore cleanup errors */ }
        }
        base.Dispose(disposing);
    }
}
