using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TodoSync.Api.Data.Entities;

[Table("sync_changes")]
[Index(nameof(TenantId), nameof(ChangeId), Name = "idx_sync_changes_tenant_changeid")]
[Index(nameof(TenantId), nameof(CreatedAt), Name = "idx_sync_changes_tenant_createdat")]
[Index(nameof(EntityId), Name = "idx_sync_changes_entityid")]
public class SyncChangeEntity
{
    [Key]
    [Column("change_id")]
    public Guid ChangeId { get; set; } = Guid.CreateVersion7();

    [Column("tenant_id")]
    [MaxLength(36)]
    public string TenantId { get; set; } = "default";

    [Column("entity_type")]
    [MaxLength(50)]
    public string EntityType { get; set; } = "todo";

    [Column("entity_id")]
    [MaxLength(36)]
    public string EntityId { get; set; } = default!;

    [Column("operation")]
    [MaxLength(20)]
    public string Operation { get; set; } = default!; // "upsert" | "delete"

    [Column("payload_json")]
    public string PayloadJson { get; set; } = default!;

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("trace_id")]
    [MaxLength(64)]
    public string? TraceId { get; set; }

    [Column("event_id")]
    [MaxLength(36)]
    public string? EventId { get; set; }
}
