using System.Text.Json;
using TodoSync.Api.Models;

namespace TodoSync.Api.Services;

public interface IEventStoreService
{
    Task<IReadOnlyList<string>> AppendEventsAsync(IEnumerable<TodoEvent> events, CancellationToken ct = default);
    Task<IReadOnlyList<TodoItem>> PullTodosSinceAsync(long since, CancellationToken ct = default);
    Task<long> GetServerTimeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct = default);
}

public sealed class EventStoreService : IEventStoreService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, TodoItem> _todos = new();
    private readonly HashSet<string> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TodoEvent> _events = [];
    private readonly string _stateFile;

    public EventStoreService(IHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _stateFile = Path.Combine(dir, "state.json");
        LoadState();
    }

    public async Task<IReadOnlyList<string>> AppendEventsAsync(IEnumerable<TodoEvent> events, CancellationToken ct = default)
    {
        var accepted = new List<string>();

        await _lock.WaitAsync(ct);
        try
        {
            foreach (var e in events.OrderBy(x => x.CreatedAt))
            {
                if (string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.TodoId) || string.IsNullOrWhiteSpace(e.Type))
                    continue;

                if (!_eventIds.Add(e.EventId))
                {
                    accepted.Add(e.EventId);
                    continue;
                }

                Apply(e);
                _events.Add(e);
                accepted.Add(e.EventId);
            }

            await SaveStateAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        return accepted;
    }

    public async Task<IReadOnlyList<TodoItem>> PullTodosSinceAsync(long since, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _todos.Values
                .Where(t => t.UpdatedAt >= since)
                .OrderByDescending(t => t.UpdatedAt)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<long> GetServerTimeAsync(CancellationToken ct = default)
        => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public async Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _todos.Values.OrderByDescending(x => x.UpdatedAt).Select(Clone).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Apply(TodoEvent e)
    {
        _todos.TryGetValue(e.TodoId, out var current);
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), e.CreatedAt);

        switch (e.Type)
        {
            case "TODO_CREATED":
                var title = ReadPayloadString(e.Payload, "title") ?? "";
                var priority = ReadPayloadString(e.Payload, "priority") ?? "MEDIUM";
                var dayKey = ReadPayloadString(e.Payload, "dayKey") ?? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
                var maxOrder = _todos.Values.Where(x => x.DayKey == dayKey).Select(x => x.SortOrder).DefaultIfEmpty(0).Max();
                _todos[e.TodoId] = new TodoItem
                {
                    Id = e.TodoId,
                    Title = title,
                    Priority = priority,
                    DayKey = dayKey,
                    SortOrder = maxOrder + 1,
                    Completed = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Deleted = false,
                };
                break;

            case "TODO_TOGGLED":
                if (current is null) return;
                current.Completed = !current.Completed;
                current.CreatedAt = current.CreatedAt == 0 ? current.UpdatedAt : current.CreatedAt;
                current.UpdatedAt = now;
                _todos[e.TodoId] = current;
                break;

            case "TODO_RENAMED":
                if (current is null) return;
                current.Title = ReadPayloadString(e.Payload, "title") ?? current.Title;
                current.Priority = ReadPayloadString(e.Payload, "priority") ?? current.Priority;
                current.CreatedAt = current.CreatedAt == 0 ? current.UpdatedAt : current.CreatedAt;
                current.UpdatedAt = now;
                _todos[e.TodoId] = current;
                break;


            case "TODO_REORDERED":
                var reorderDayKey = ReadPayloadString(e.Payload, "dayKey");
                var orderedIds = ReadPayloadStringArray(e.Payload, "orderedIds");
                if (!string.IsNullOrWhiteSpace(reorderDayKey) && orderedIds is not null)
                {
                    for (int i = 0; i < orderedIds.Count; i++)
                    {
                        var id = orderedIds[i];
                        if (_todos.TryGetValue(id, out var row) && row.DayKey == reorderDayKey)
                        {
                            row.SortOrder = i + 1;
                            row.UpdatedAt = now;
                            _todos[id] = row;
                        }
                    }
                }
                break;

            case "TODO_DELETED":
                if (current is null) return;
                current.Deleted = true;
                current.CreatedAt = current.CreatedAt == 0 ? current.UpdatedAt : current.CreatedAt;
                current.UpdatedAt = now;
                _todos[e.TodoId] = current;
                break;

            case "TODO_UPSERTED_FROM_SERVER":
                var incoming = ReadPayloadTodo(e.Payload);
                if (incoming is null) return;
                if (current is null || incoming.UpdatedAt >= current.UpdatedAt)
                {
                    incoming.CreatedAt = incoming.CreatedAt == 0 ? incoming.UpdatedAt : incoming.CreatedAt;
                    _todos[e.TodoId] = incoming;
                }
                break;
        }
    }

    private static string? ReadPayloadString(JsonElement? payload, string property)
    {
        if (payload is null) return null;
        if (payload.Value.ValueKind != JsonValueKind.Object) return null;
        return payload.Value.TryGetProperty(property, out var p) ? p.GetString() : null;
    }


    private static List<string>? ReadPayloadStringArray(JsonElement? payload, string property)
    {
        if (payload is null) return null;
        if (payload.Value.ValueKind != JsonValueKind.Object) return null;
        if (!payload.Value.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.Array) return null;
        var result = new List<string>();
        foreach (var x in p.EnumerateArray())
        {
            var s = x.GetString();
            if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
        }
        return result;
    }

    private static TodoItem? ReadPayloadTodo(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return payload.Value.Deserialize<TodoItem>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private sealed class PersistedState
    {
        public List<TodoItem> Todos { get; set; } = [];
        public List<TodoEvent> Events { get; set; } = [];
    }

    private void LoadState()
    {
        if (!File.Exists(_stateFile)) return;
        try
        {
            var raw = File.ReadAllText(_stateFile);
            var state = JsonSerializer.Deserialize<PersistedState>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state is null) return;

            foreach (var t in state.Todos)
                _todos[t.Id] = t;

            foreach (var e in state.Events)
            {
                _events.Add(e);
                _eventIds.Add(e.EventId);
            }
        }
        catch
        {
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        var state = new PersistedState
        {
            Todos = _todos.Values.ToList(),
            Events = _events.TakeLast(10000).ToList(),
        };

        var raw = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_stateFile, raw, ct);
    }

    private static TodoItem Clone(TodoItem t) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Priority = t.Priority,
        DayKey = t.DayKey,
        SortOrder = t.SortOrder,
        Completed = t.Completed,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        Deleted = t.Deleted,
    };
}
