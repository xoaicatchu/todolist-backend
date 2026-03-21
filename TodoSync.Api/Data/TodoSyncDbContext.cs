using Microsoft.EntityFrameworkCore;
using TodoSync.Api.Data.Entities;

namespace TodoSync.Api.Data;

public class TodoSyncDbContext : DbContext
{
    public TodoSyncDbContext(DbContextOptions<TodoSyncDbContext> options) : base(options)
    {
    }

    public DbSet<TodoEntity> Todos => Set<TodoEntity>();
    public DbSet<SyncChangeEntity> SyncChanges => Set<SyncChangeEntity>();

    public DbSet<ProcessedEventEntity> ProcessedEvents => Set<ProcessedEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // TodoEntity
        modelBuilder.Entity<TodoEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.UpdatedAt }).HasDatabaseName("idx_todos_tenant_updated");
            entity.HasIndex(e => new { e.TenantId, e.DayKey, e.SortOrder }).HasDatabaseName("idx_todos_tenant_day_sort");
            entity.Property(e => e.Version).IsConcurrencyToken();
        });

        // SyncChangeEntity
        modelBuilder.Entity<SyncChangeEntity>(entity =>
        {
            entity.HasKey(e => e.ChangeId);
            entity.HasIndex(e => new { e.TenantId, e.ChangeId }).HasDatabaseName("idx_sync_changes_tenant_changeid");
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt }).HasDatabaseName("idx_sync_changes_tenant_createdat");
            entity.HasIndex(e => e.EntityId).HasDatabaseName("idx_sync_changes_entityid");
        });


        // ProcessedEventEntity
        modelBuilder.Entity<ProcessedEventEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.EventId).IsUnique().HasDatabaseName("idx_processed_eventid");
        });
    }
}
