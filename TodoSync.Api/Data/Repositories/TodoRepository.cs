using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TodoSync.Api.Data.Entities;
using TodoSync.Api.Models;

namespace TodoSync.Api.Data.Repositories;

public interface ITodoRepository
{
    Task<TodoEntity?> GetByIdAsync(string id, string tenantId = "default", CancellationToken ct = default);
    Task<List<TodoEntity>> GetByIdsAsync(IEnumerable<string> ids, string tenantId = "default", CancellationToken ct = default);
    Task<List<TodoEntity>> GetAllAsync(string tenantId = "default", CancellationToken ct = default);
    Task<List<TodoEntity>> GetByDayKeyAsync(string dayKey, string tenantId = "default", CancellationToken ct = default);
    Task<TodoEntity> CreateAsync(TodoEntity todo, CancellationToken ct = default);
    Task<TodoEntity> UpdateAsync(TodoEntity todo, CancellationToken ct = default);
    Task DeleteAsync(string id, string tenantId = "default", CancellationToken ct = default);
    Task<List<TodoEntity>> GetUpdatedSinceAsync(long since, string tenantId = "default", CancellationToken ct = default);
}

public class TodoRepository : ITodoRepository
{
    private readonly TodoSyncDbContext _context;

    public TodoRepository(TodoSyncDbContext context)
    {
        _context = context;
    }

    public async Task<TodoEntity?> GetByIdAsync(string id, string tenantId = "default", CancellationToken ct = default)
    {
        return await _context.Todos
            .AsNoTracking()
            .Where(t => t.Id == id && t.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<TodoEntity>> GetByIdsAsync(IEnumerable<string> ids, string tenantId = "default", CancellationToken ct = default)
    {
        return await _context.Todos
            .Where(t => t.TenantId == tenantId && ids.Contains(t.Id))
            .ToListAsync(ct);
    }

    public async Task<List<TodoEntity>> GetAllAsync(string tenantId = "default", CancellationToken ct = default)
    {
        return await _context.Todos
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && !t.Deleted)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<TodoEntity>> GetByDayKeyAsync(string dayKey, string tenantId = "default", CancellationToken ct = default)
    {
        return await _context.Todos
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.DayKey == dayKey && !t.Deleted)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<TodoEntity> CreateAsync(TodoEntity todo, CancellationToken ct = default)
    {
        _context.Todos.Add(todo);
        return todo;
    }

    public async Task<TodoEntity> UpdateAsync(TodoEntity todo, CancellationToken ct = default)
    {
        _context.Todos.Update(todo);
        return todo;
    }

    public async Task DeleteAsync(string id, string tenantId = "default", CancellationToken ct = default)
    {
        // Use tracked query (no AsNoTracking) since we need to modify the entity
        var todo = await _context.Todos
            .Where(t => t.Id == id && t.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (todo != null)
        {
            todo.Deleted = true;
            todo.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    public async Task<List<TodoEntity>> GetUpdatedSinceAsync(long since, string tenantId = "default", CancellationToken ct = default)
    {
        return await _context.Todos
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.UpdatedAt >= since)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);
    }
}
