using Microsoft.EntityFrameworkCore;
using TodoSync.Api.Data.Entities;

namespace TodoSync.Api.Data.Repositories;

public sealed record SyncChangePosition(long Sequence, Guid ChangeId);

public interface ISyncChangeRepository
{
    // Legacy Todo reader retained for callers outside the generic engine.
    Task<List<SyncChangeEntity>> GetChangesSinceAsync(
        Guid? sinceChangeId,
        int limit,
        string tenantId = "default",
        CancellationToken ct = default);

    Task<Guid?> GetLatestChangeIdAsync(
        string tenantId = "default",
        CancellationToken ct = default);

    // Generic cursor reader. Sequence is internal; change_id remains the public checkpoint.
    Task<List<SyncChangeEntity>> GetChangesWindowAsync(
        long afterSequence,
        long upperBoundSequence,
        int limit,
        IReadOnlyCollection<string> entityTypes,
        string tenantId = "default",
        CancellationToken ct = default);

    Task<SyncChangePosition?> GetLatestPositionAsync(
        IReadOnlyCollection<string> entityTypes,
        string tenantId = "default",
        CancellationToken ct = default);

    Task<long?> GetSequenceAsync(
        Guid changeId,
        IReadOnlyCollection<string> entityTypes,
        string tenantId = "default",
        CancellationToken ct = default);

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
            query = query.Where(c => c.ChangeId.CompareTo(sinceChangeId.Value) > 0);

        return await query
            .OrderBy(c => c.ChangeId)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<Guid?> GetLatestChangeIdAsync(
        string tenantId = "default",
        CancellationToken ct = default)
    {
        var latest = await _context.SyncChanges
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.ChangeId)
            .Select(c => c.ChangeId)
            .FirstOrDefaultAsync(ct);

        return latest == Guid.Empty ? null : latest;
    }

    public Task<List<SyncChangeEntity>> GetChangesWindowAsync(
        long afterSequence,
        long upperBoundSequence,
        int limit,
        IReadOnlyCollection<string> entityTypes,
        string tenantId = "default",
        CancellationToken ct = default) =>
        _context.SyncChanges
            .AsNoTracking()
            .Where(c =>
                c.TenantId == tenantId &&
                entityTypes.Contains(c.EntityType) &&
                c.ChangeSequence > afterSequence &&
                c.ChangeSequence <= upperBoundSequence)
            .OrderBy(c => c.ChangeSequence)
            .Take(limit)
            .ToListAsync(ct);

    public Task<SyncChangePosition?> GetLatestPositionAsync(
        IReadOnlyCollection<string> entityTypes,
        string tenantId = "default",
        CancellationToken ct = default) =>
        _context.SyncChanges
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && entityTypes.Contains(c.EntityType))
            .OrderByDescending(c => c.ChangeSequence)
            .Select(c => new SyncChangePosition(c.ChangeSequence, c.ChangeId))
            .FirstOrDefaultAsync(ct);

    public Task<long?> GetSequenceAsync(
        Guid changeId,
        IReadOnlyCollection<string> entityTypes,
        string tenantId = "default",
        CancellationToken ct = default) =>
        _context.SyncChanges
            .AsNoTracking()
            .Where(c =>
                c.ChangeId == changeId &&
                c.TenantId == tenantId &&
                entityTypes.Contains(c.EntityType))
            .Select(c => (long?)c.ChangeSequence)
            .SingleOrDefaultAsync(ct);

    public Task<SyncChangeEntity> CreateAsync(
        SyncChangeEntity change,
        CancellationToken ct = default)
    {
        _context.SyncChanges.Add(change);
        return Task.FromResult(change);
    }

    public Task<List<SyncChangeEntity>> CreateBatchAsync(
        List<SyncChangeEntity> changes,
        CancellationToken ct = default)
    {
        _context.SyncChanges.AddRange(changes);
        return Task.FromResult(changes);
    }
}
