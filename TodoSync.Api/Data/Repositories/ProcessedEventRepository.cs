using Microsoft.EntityFrameworkCore;
using TodoSync.Api.Data.Entities;

namespace TodoSync.Api.Data.Repositories;

public interface IProcessedEventRepository
{
    Task<bool> IsProcessedAsync(string eventId, CancellationToken ct = default);
    Task<List<string>> GetProcessedEventIdsAsync(IEnumerable<string> eventIds, CancellationToken ct = default);
    Task MarkProcessedAsync(string eventId, string tenantId = "default", CancellationToken ct = default);
}

public class ProcessedEventRepository : IProcessedEventRepository
{
    private readonly TodoSyncDbContext _context;

    public ProcessedEventRepository(TodoSyncDbContext context)
    {
        _context = context;
    }

    public async Task<bool> IsProcessedAsync(string eventId, CancellationToken ct = default)
    {
        return await _context.ProcessedEvents.AsNoTracking().AnyAsync(e => e.EventId == eventId, ct);
    }

    public async Task<List<string>> GetProcessedEventIdsAsync(IEnumerable<string> eventIds, CancellationToken ct = default)
    {
        return await _context.ProcessedEvents
            .Where(e => eventIds.Contains(e.EventId))
            .Select(e => e.EventId)
            .ToListAsync(ct);
    }

    public async Task MarkProcessedAsync(string eventId, string tenantId = "default", CancellationToken ct = default)
    {
        var processed = new ProcessedEventEntity
        {
            EventId = eventId,
            TenantId = tenantId,
            ProcessedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _context.ProcessedEvents.Add(processed);
    }
}
