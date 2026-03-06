using System.Text.Json;

namespace TodoSync.Api.Models;

public sealed class TodoEvent
{
    public required string EventId { get; set; }
    public required string Type { get; set; }
    public required string TodoId { get; set; }
    public JsonElement? Payload { get; set; }
    public long CreatedAt { get; set; }
    public int Synced { get; set; }
}
