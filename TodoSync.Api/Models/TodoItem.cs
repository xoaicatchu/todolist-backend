namespace TodoSync.Api.Models;

public sealed class TodoItem
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string Priority { get; set; } = "MEDIUM";
    public string DayKey { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
    public bool Completed { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}
