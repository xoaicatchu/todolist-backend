using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using TodoSync.Api.Models;
using TodoSync.Api.Services;
using Xunit;

namespace TodoSync.Tests.Integration;

public class SyncApiTests : IClassFixture<TodoSyncWebApplicationFactory>
{
    private readonly TodoSyncWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SyncApiTests(TodoSyncWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.EnsureDatabaseCreated();
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthCheck_ShouldReturnOk()
    {
        // Act
        var response = await _client.GetAsync("/");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("TodoSync.Api");
        content.Should().Contain("ok");
    }

    [Fact]
    public async Task PushAndPull_BasicFlow_ShouldWork()
    {
        // Arrange - Create a todo
        var todoId = Guid.NewGuid().ToString();
        var eventId = Guid.NewGuid().ToString();
        var pushRequest = new SyncPushRequest
        {
            Events = new List<TodoEvent>
            {
                new()
                {
                    EventId = eventId,
                    Type = "TODO_CREATED",
                    TodoId = todoId,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        title = "Integration Test Todo",
                        priority = "HIGH",
                        dayKey = "2026-03-14"
                    }),
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            }
        };

        // Act - Push via V2 endpoint
        var pushResponse = await _client.PostAsJsonAsync("/api/v2/sync/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        await WaitUntilAsync(async () => 
        {
            var r = await _client.GetAsync("/api/v2/sync/all");
            if (!r.IsSuccessStatusCode) return false;
            var t = await r.Content.ReadFromJsonAsync<List<TodoItem>>();
            return t != null && t.Any(x => x.Id == todoId);
        });

        var pushResult = await pushResponse.Content.ReadFromJsonAsync<SyncPushResponse>();
        pushResult.Should().NotBeNull();
        pushResult!.AcceptedEventIds.Should().ContainSingle().Which.Should().Be(eventId);

        // Act - Pull via V2 endpoint
        var pullResponse = await _client.GetAsync("/api/v2/sync/pull");
        pullResponse.EnsureSuccessStatusCode();

        var pullResult = await pullResponse.Content.ReadFromJsonAsync<SyncPullV2Response>();
        pullResult.Should().NotBeNull();
        pullResult!.Changes.Should().Contain(c =>
            c.EntityId == todoId &&
            c.Payload != null &&
            c.Payload.Title == "Integration Test Todo" &&
            c.Payload.Priority == "HIGH");
    }

    [Fact]
    public async Task PushV2Pull_WithPagination_ShouldWork()
    {
        // Arrange - Create multiple todos
        var todoIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid().ToString()).ToList();
        var events = todoIds.Select((id, i) => new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_CREATED",
            TodoId = id,
            Payload = JsonSerializer.SerializeToElement(new
            {
                title = $"Todo {i}",
                priority = "MEDIUM",
                dayKey = "2026-03-14"
            }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + i
        }).ToList();

        var pushRequest = new SyncPushRequest { Events = events };

        // Act - Push via V2 endpoint
        var pushResponse = await _client.PostAsJsonAsync("/api/v2/sync/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        await WaitUntilAsync(async () => 
        {
            var r = await _client.GetAsync("/api/v2/sync/all");
            if (!r.IsSuccessStatusCode) return false;
            var t = await r.Content.ReadFromJsonAsync<List<TodoItem>>();
            // Ensure all 10 have been processed
            return t != null && t.Count(x => todoIds.Contains(x.Id)) >= 10;
        });

        // Act - Pull with pagination (limit 5) via V2 endpoint
        var pullResponse = await _client.GetAsync("/api/v2/sync/pull?limit=5");
        pullResponse.EnsureSuccessStatusCode();

        var pullResult = await pullResponse.Content.ReadFromJsonAsync<SyncPullV2Response>();
        pullResult.Should().NotBeNull();
        pullResult!.Changes.Should().HaveCountGreaterThanOrEqualTo(5);
        pullResult.HasMore.Should().BeTrue();
        pullResult.NextCursor.Should().NotBeNullOrEmpty();

        // Act - Pull second page using sinceChangeId (from NextCursor which is the last ChangeId)
        var sinceId = pullResult.NextCursor;
        var pullResponse2 = await _client.GetAsync($"/api/v2/sync/pull?limit=5&sinceChangeId={sinceId}");
        pullResponse2.EnsureSuccessStatusCode();

        var pullResult2 = await pullResponse2.Content.ReadFromJsonAsync<SyncPullV2Response>();
        pullResult2.Should().NotBeNull();
        pullResult2!.Changes.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task PushIdempotent_DuplicateEvents_ShouldNotDuplicate()
    {
        // Arrange
        var todoId = Guid.NewGuid().ToString();
        var eventId = Guid.NewGuid().ToString();
        var pushRequest = new SyncPushRequest
        {
            Events = new List<TodoEvent>
            {
                new()
                {
                    EventId = eventId,
                    Type = "TODO_CREATED",
                    TodoId = todoId,
                    Payload = JsonSerializer.SerializeToElement(new { title = "Idempotent Test" }),
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            }
        };

        // Act - Push same event twice via V2 endpoint
        await _client.PostAsJsonAsync("/api/v2/sync/push", pushRequest);
        await _client.PostAsJsonAsync("/api/v2/sync/push", pushRequest);

        await WaitUntilAsync(async () => 
        {
            var r = await _client.GetAsync("/api/v2/sync/all");
            if (!r.IsSuccessStatusCode) return false;
            var t = await r.Content.ReadFromJsonAsync<List<TodoItem>>();
            return t != null && t.Any(x => x.Id == todoId);
        });

        // Assert - Should only have one todo with that id
        var allResponse = await _client.GetAsync("/api/v2/sync/all");
        var allTodos = await allResponse.Content.ReadFromJsonAsync<List<TodoItem>>();
        allTodos.Should().NotBeNull();
        allTodos!.Count(t => t.Id == todoId).Should().Be(1);
    }

    [Fact]
    public async Task TodoLifecycle_CreateToggleRenameDelete_ShouldWork()
    {
        // Arrange
        var todoId = Guid.NewGuid().ToString();

        // Act 1 - Create
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_CREATED",
            TodoId = todoId,
            Payload = JsonSerializer.SerializeToElement(new { title = "Lifecycle Test" }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await WaitUntilAsync(async () => await GetTodo(todoId) != null);

        var todo1 = await GetTodo(todoId);
        todo1.Should().NotBeNull("newly created todo should appear in /api/v2/sync/all");
        todo1!.Title.Should().Be("Lifecycle Test");
        todo1.Completed.Should().BeFalse();

        // Act 2 - Toggle
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_TOGGLED",
            TodoId = todoId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await WaitUntilAsync(async () => (await GetTodo(todoId))?.Completed == true);

        var todo2 = await GetTodo(todoId);
        todo2!.Completed.Should().BeTrue();

        // Act 3 - Rename
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_RENAMED",
            TodoId = todoId,
            Payload = JsonSerializer.SerializeToElement(new { title = "Updated Title", priority = "LOW" }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await WaitUntilAsync(async () => (await GetTodo(todoId))?.Title == "Updated Title");

        var todo3 = await GetTodo(todoId);
        todo3!.Title.Should().Be("Updated Title");
        todo3.Priority.Should().Be("LOW");

        // Act 4 - Delete
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_DELETED",
            TodoId = todoId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await WaitUntilAsync(async () => await GetTodo(todoId) == null);

        // Deleted todos are filtered from GetAllAsync (!t.Deleted), so it should be null
        var todo4 = await GetTodo(todoId);
        todo4.Should().BeNull("deleted todo should not appear in /api/v2/sync/all");
    }

    [Fact]
    public async Task SignalR_PushEvent_ShouldTriggerNotification()
    {
        // Arrange
        var hubUrl = _client.BaseAddress!.ToString().TrimEnd('/') + "/hubs/sync";
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        var notificationReceived = new TaskCompletionSource<bool>();
        connection.On<object>("todosChanged", _ => notificationReceived.SetResult(true));

        await connection.StartAsync();

        // Act - Push an event via V2 endpoint
        var todoId = Guid.NewGuid().ToString();
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_CREATED",
            TodoId = todoId,
            Payload = JsonSerializer.SerializeToElement(new { title = "SignalR Test" }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        // Assert - Should receive notification
        var received = await Task.WhenAny(notificationReceived.Task, Task.Delay(5000));
        received.Should().Be(notificationReceived.Task);
        (await notificationReceived.Task).Should().BeTrue();

        await connection.StopAsync();
    }

    private async Task PushEvent(TodoEvent evt)
    {
        var request = new SyncPushRequest { Events = new List<TodoEvent> { evt } };
        var response = await _client.PostAsJsonAsync("/api/v2/sync/push", request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<TodoItem?> GetTodo(string todoId)
    {
        var response = await _client.GetAsync("/api/v2/sync/all");
        var todos = await response.Content.ReadFromJsonAsync<List<TodoItem>>();
        return todos?.FirstOrDefault(t => t.Id == todoId);
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, int maxWaitMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
    }
}
