using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TodoSync.Api.Data.Entities;

[Table("todos")]
public class TodoEntity
{
    [Key]
    [Column("id")]
    [MaxLength(36)]
    public string Id { get; set; } = default!;

    [Column("tenant_id")]
    [MaxLength(36)]
    public string TenantId { get; set; } = "default";

    [Column("title")]
    [MaxLength(500)]
    public string Title { get; set; } = default!;

    [Column("priority")]
    [MaxLength(20)]
    public string Priority { get; set; } = "MEDIUM";

    [Column("day_key")]
    [MaxLength(10)]
    public string DayKey { get; set; } = default!;

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("completed")]
    public bool Completed { get; set; }

    [Column("deleted")]
    public bool Deleted { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("updated_at")]
    public long UpdatedAt { get; set; }

    [Column("version")]
    public int Version { get; set; }
}
