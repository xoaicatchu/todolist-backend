using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using TodoSync.Api.Models;
using Xunit;

namespace TodoSync.Tests.Integration;

public class SyncApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SyncApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
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

        // Act - Push
        var pushResponse = await _client.PostAsJsonAsync("/api/sync/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        var pushResult = await pushResponse.Content.ReadFromJsonAsync<SyncPushResponse>();
        pushResult.Should().NotBeNull();
        pushResult!.AcceptedEventIds.Should().ContainSingle().Which.Should().Be(eventId);

        // Act - Pull
        var pullResponse = await _client.GetAsync("/api/sync/pull?since=0");
        pullResponse.EnsureSuccessStatusCode();

        var pullResult = await pullResponse.Content.ReadFromJsonAsync<SyncPullResponse>();
        pullResult.Should().NotBeNull();
        pullResult!.Todos.Should().ContainSingle()
            .Which.Should().Match<TodoItem>(t =>
                t.Id == todoId &&
                t.Title == "Integration Test Todo" &&
                t.Priority == "HIGH");
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

        // Act - Push
        await _client.PostAsJsonAsync("/api/sync/push", pushRequest);

        // Act - Pull with pagination (limit 5)
        var pullResponse = await _client.GetAsync("/api/sync/v2/pull?sinceChangeId=0&limit=5");
        pullResponse.EnsureSuccessStatusCode();

        var pullResult = await pullResponse.Content.ReadFromJsonAsync<JsonElement>();
        var changes = pullResult.GetProperty("changes").EnumerateArray().ToList();
        var hasMore = pullResult.GetProperty("hasMore").GetBoolean();
        var nextCursor = pullResult.GetProperty("nextCursor").GetString();

        // Assert - First page
        changes.Should().HaveCount(5);
        hasMore.Should().BeTrue();
        nextCursor.Should().NotBeNullOrEmpty();

        // Act - Pull second page
        var pullResponse2 = await _client.GetAsync($"/api/sync/v2/pull?sinceChangeId=0&limit=5&cursor={nextCursor}");
        pullResponse2.EnsureSuccessStatusCode();

        var pullResult2 = await pullResponse2.Content.ReadFromJsonAsync<JsonElement>();
        var changes2 = pullResult2.GetProperty("changes").EnumerateArray().ToList();
        var hasMore2 = pullResult2.GetProperty("hasMore").GetBoolean();

        // Assert - Second page
        changes2.Should().HaveCount(5);
        hasMore2.Should().BeFalse();
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

        // Act - Push same event twice
        await _client.PostAsJsonAsync("/api/sync/push", pushRequest);
        await _client.PostAsJsonAsync("/api/sync/push", pushRequest);

        // Assert - Should only have one todo
        var allResponse = await _client.GetAsync("/api/sync/all");
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

        var todo1 = await GetTodo(todoId);
        todo1.Should().NotBeNull();
        todo1!.Title.Should().Be("Lifecycle Test");
        todo1.Completed.Should().BeFalse();

        // Act 2 - Toggle
        await Task.Delay(50);
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_TOGGLED",
            TodoId = todoId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var todo2 = await GetTodo(todoId);
        todo2!.Completed.Should().BeTrue();

        // Act 3 - Rename
        await Task.Delay(50);
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_RENAMED",
            TodoId = todoId,
            Payload = JsonSerializer.SerializeToElement(new { title = "Updated Title", priority = "LOW" }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var todo3 = await GetTodo(todoId);
        todo3!.Title.Should().Be("Updated Title");
        todo3.Priority.Should().Be("LOW");

        // Act 4 - Delete
        await Task.Delay(50);
        await PushEvent(new TodoEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Type = "TODO_DELETED",
            TodoId = todoId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var todo4 = await GetTodo(todoId);
        todo4!.Deleted.Should().BeTrue();
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

        // Act - Push an event
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
        notificationReceived.Task.Result.Should().BeTrue();

        await connection.StopAsync();
    }

    private async Task PushEvent(TodoEvent evt)
    {
        var request = new SyncPushRequest { Events = new List<TodoEvent> { evt } };
        var response = await _client.PostAsJsonAsync("/api/sync/push", request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<TodoItem?> GetTodo(string todoId)
    {
        var response = await _client.GetAsync("/api/sync/all");
        var todos = await response.Content.ReadFromJsonAsync<List<TodoItem>>();
        return todos?.FirstOrDefault(t => t.Id == todoId);
    }
}
