using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using TodoSync.Api.Models;
using TodoSync.Api.Services;
using Xunit;
using FluentAssertions;

namespace TodoSync.Tests.Services;

public class EventStoreServiceTests : IDisposable
{
    private readonly EventStoreService _service;
    private readonly string _testDirectory;

    public EventStoreServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"todosync_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        var env = new TestHostEnvironment(_testDirectory);
        _service = new EventStoreService(env);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task AppendEvents_CreateTodo_ShouldSucceed()
    {
        // Arrange
        var eventId = Guid.NewGuid().ToString();
        var todoId = Guid.NewGuid().ToString();
        var events = new List<TodoEvent>
        {
            new()
            {
                EventId = eventId,
                Type = "TODO_CREATED",
                TodoId = todoId,
                Payload = JsonSerializer.SerializeToElement(new { title = "Test Todo", priority = "HIGH" }),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };

        // Act
        var accepted = await _service.AppendEventsAsync(events);

        // Assert
        accepted.Should().ContainSingle().Which.Should().Be(eventId);

        var all = await _service.GetAllAsync();
        all.Should().ContainSingle()
            .Which.Should().Match<TodoItem>(t =>
                t.Id == todoId &&
                t.Title == "Test Todo" &&
                t.Priority == "HIGH" &&
                !t.Completed &&
                !t.Deleted);
    }

    [Fact]
    public async Task AppendEvents_DuplicateEventId_ShouldBeIdempotent()
    {
        // Arrange
        var eventId = Guid.NewGuid().ToString();
        var todoId = Guid.NewGuid().ToString();
        var events = new List<TodoEvent>
        {
            new()
            {
                EventId = eventId,
                Type = "TODO_CREATED",
                TodoId = todoId,
                Payload = JsonSerializer.SerializeToElement(new { title = "Test Todo" }),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };

        // Act
        var accepted1 = await _service.AppendEventsAsync(events);
        var accepted2 = await _service.AppendEventsAsync(events); // Duplicate

        // Assert
        accepted1.Should().ContainSingle();
        accepted2.Should().ContainSingle();

        var all = await _service.GetAllAsync();
        all.Should().ContainSingle(); // Only one todo should exist
    }

    [Fact]
    public async Task AppendEvents_ToggleTodo_ShouldUpdateState()
    {
        // Arrange
        var todoId = Guid.NewGuid().ToString();
        await CreateTodo(todoId, "Test Todo");

        var toggleEvent = new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_TOGGLED",
            TodoId = todoId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Act
        await _service.AppendEventsAsync(new[] { toggleEvent });

        // Assert
        var all = await _service.GetAllAsync();
        all.Should().ContainSingle().Which.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task AppendEvents_RenameTodo_ShouldUpdateTitleAndPriority()
    {
        // Arrange
        var todoId = Guid.NewGuid().ToString();
        await CreateTodo(todoId, "Original Title");

        var renameEvent = new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_RENAMED",
            TodoId = todoId,
            Payload = JsonSerializer.SerializeToElement(new { title = "Updated Title", priority = "LOW" }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Act
        await _service.AppendEventsAsync(new[] { renameEvent });

        // Assert
        var all = await _service.GetAllAsync();
        all.Should().ContainSingle()
            .Which.Should().Match<TodoItem>(t =>
                t.Title == "Updated Title" &&
                t.Priority == "LOW");
    }

    [Fact]
    public async Task AppendEvents_DeleteTodo_ShouldMarkAsDeleted()
    {
        // Arrange
        var todoId = Guid.NewGuid().ToString();
        await CreateTodo(todoId, "Test Todo");

        var deleteEvent = new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_DELETED",
            TodoId = todoId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Act
        await _service.AppendEventsAsync(new[] { deleteEvent });

        // Assert
        var all = await _service.GetAllAsync();
        all.Should().ContainSingle().Which.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task AppendEvents_ReorderTodos_ShouldUpdateSortOrder()
    {
        // Arrange
        var dayKey = "2026-03-14";
        var todo1 = Guid.NewGuid().ToString();
        var todo2 = Guid.NewGuid().ToString();
        var todo3 = Guid.NewGuid().ToString();

        await CreateTodo(todo1, "Todo 1", dayKey);
        await CreateTodo(todo2, "Todo 2", dayKey);
        await CreateTodo(todo3, "Todo 3", dayKey);

        var reorderEvent = new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_REORDERED",
            TodoId = todo1, // Not used for reorder, but required
            Payload = JsonSerializer.SerializeToElement(new
            {
                dayKey = dayKey,
                orderedIds = new[] { todo3, todo1, todo2 }
            }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Act
        await _service.AppendEventsAsync(new[] { reorderEvent });

        // Assert
        var all = await _service.GetAllAsync();
        var sorted = all.Where(t => t.DayKey == dayKey).OrderBy(t => t.SortOrder).ToList();
        sorted[0].Id.Should().Be(todo3);
        sorted[1].Id.Should().Be(todo1);
        sorted[2].Id.Should().Be(todo2);
    }

    [Fact]
    public async Task PullTodosSince_ShouldReturnOnlyModifiedTodos()
    {
        // Arrange
        var todoId1 = Guid.NewGuid().ToString();
        var todoId2 = Guid.NewGuid().ToString();

        await CreateTodo(todoId1, "Todo 1");
        await Task.Delay(100); // Ensure different timestamps

        var checkpoint = await _service.GetServerTimeAsync();
        await Task.Delay(100);

        await CreateTodo(todoId2, "Todo 2");

        // Act
        var modified = await _service.PullTodosSinceAsync(checkpoint);

        // Assert
        modified.Should().ContainSingle().Which.Id.Should().Be(todoId2);
    }

    [Fact]
    public async Task ConcurrentAppendEvents_ShouldHandleSafely()
    {
        // Arrange
        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            var todoId = Guid.NewGuid().ToString();
            var events = new List<TodoEvent>
            {
                new()
                {
                    EventId = Guid.NewGuid().ToString(),
                    Type = "TODO_CREATED",
                    TodoId = todoId,
                    Payload = JsonSerializer.SerializeToElement(new { title = $"Todo {i}" }),
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            };
            await _service.AppendEventsAsync(events);
        });

        // Act
        await Task.WhenAll(tasks);

        // Assert
        var all = await _service.GetAllAsync();
        all.Should().HaveCount(50);
    }

    [Fact]
    public async Task StateSnapshot_ShouldPersistAndRestore()
    {
        // Arrange
        var todoId = Guid.NewGuid().ToString();
        await CreateTodo(todoId, "Persistent Todo");

        // Act - Create new service instance (simulates restart)
        var env = new TestHostEnvironment(_testDirectory);
        var newService = new EventStoreService(env);

        // Assert
        var all = await newService.GetAllAsync();
        all.Should().ContainSingle()
            .Which.Should().Match<TodoItem>(t =>
                t.Id == todoId &&
                t.Title == "Persistent Todo");
    }

    private async Task CreateTodo(string todoId, string title, string? dayKey = null)
    {
        var events = new List<TodoEvent>
        {
            new()
            {
                EventId = Guid.NewGuid().ToString(),
                Type = "TODO_CREATED",
                TodoId = todoId,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    title = title,
                    priority = "MEDIUM",
                    dayKey = dayKey ?? "2026-03-14"
                }),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
        await _service.AppendEventsAsync(events);
    }

    private class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "TodoSync.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
