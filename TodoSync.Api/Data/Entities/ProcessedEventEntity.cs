using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TodoSync.Api.Data.Entities;

/// <summary>
/// Tracks processed event IDs for idempotency
/// </summary>
[Table("processed_events")]
[Index(nameof(EventId), Name = "idx_processed_eventid", IsUnique = true)]
public class ProcessedEventEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Column("event_id")]
    [MaxLength(36)]
    public string EventId { get; set; } = default!;

    [Column("processed_at")]
    public long ProcessedAt { get; set; }

    [Column("tenant_id")]
    [MaxLength(36)]
    public string TenantId { get; set; } = "default";
}
