using Microsoft.EntityFrameworkCore;
using TodoSync.Api.Data.Entities;

namespace TodoSync.Api.Data.Repositories;

public interface ISyncChangeRepository
{
    Task<List<SyncChangeEntity>> GetChangesSinceAsync(Guid? sinceChangeId, int limit, string tenantId = "default", CancellationToken ct = default);
    Task<Guid?> GetLatestChangeIdAsync(string tenantId = "default", CancellationToken ct = default);
    Task<SyncChangeEntity> CreateAsync(SyncChangeEntity change, CancellationToken ct = default);
    Task<List<SyncChangeEntity>> CreateBatchAsync(List<SyncChangeEntity> changes, CancellationToken ct = default);
}

public class SyncChangeRepository : ISyncChangeRepository
{
    private readonly TodoSyncDbContext _context;

    public SyncChangeRepository(TodoSyncDbContext context)
    {
        _context = context;
    }

    public async Task<List<SyncChangeEntity>> GetChangesSinceAsync(
        Guid? sinceChangeId,
        int limit,
        string tenantId = "default",
        CancellationToken ct = default)
    {
        var query = _context.SyncChanges.AsNoTracking().Where(c => c.TenantId == tenantId);
        
        if (sinceChangeId.HasValue && sinceChangeId.Value != Guid.Empty)
        {
            query = query.Where(c => c.ChangeId.CompareTo(sinceChangeId.Value) > 0);
        }

        return await query
            .OrderBy(c => c.ChangeId)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<Guid?> GetLatestChangeIdAsync(string tenantId = "default", CancellationToken ct = default)
    {
        var latest = await _context.SyncChanges
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.ChangeId)
            .Select(c => c.ChangeId)
            .FirstOrDefaultAsync(ct);

        return latest == Guid.Empty ? null : latest;
    }

    public async Task<SyncChangeEntity> CreateAsync(SyncChangeEntity change, CancellationToken ct = default)
    {
        _context.SyncChanges.Add(change);
        return change;
    }

    public async Task<List<SyncChangeEntity>> CreateBatchAsync(List<SyncChangeEntity> changes, CancellationToken ct = default)
    {
        _context.SyncChanges.AddRange(changes);
        return changes;
    }
}
