using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using TodoSync.Api.Models;

namespace TodoSync.LoadTest;

class Program
{
    private static readonly HttpClient _httpClient = new();
    private static string _baseUrl = "http://localhost:3000";
    private static int _totalRequests = 0;
    private static int _successfulRequests = 0;
    private static int _failedRequests = 0;
    private static readonly List<double> _latencies = new();
    private static readonly object _lock = new();

    static async Task Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("  TodoSync API Load Test Tool");
        Console.WriteLine("=================================================\n");

        if (args.Length > 0)
        {
            _baseUrl = args[0];
        }

        Console.WriteLine($"Target URL: {_baseUrl}");
        Console.WriteLine($"Starting health check...\n");

        // Health check
        try
        {
            var healthRes = await _httpClient.GetAsync($"{_baseUrl}/");
            if (!healthRes.IsSuccessStatusCode)
            {
                Console.WriteLine("❌ Health check failed! Is the server running?");
                return;
            }
            Console.WriteLine("✅ Health check passed!\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Cannot connect to server: {ex.Message}");
            return;
        }

        Console.WriteLine("Select test type:");
        Console.WriteLine("1. Smoke Test (10 users, 1 min)");
        Console.WriteLine("2. Light Load Test (50 users, 2 min)");
        Console.WriteLine("3. Medium Load Test (100 users, 3 min)");
        Console.WriteLine("4. Heavy Load Test (200 users, 5 min)");
        Console.WriteLine("5. Stress Test (500 users, 5 min)");
        Console.WriteLine("6. Extreme Stress Test (1000 users, 10 min)");
        Console.WriteLine("7. Custom Test");
        Console.Write("\nEnter choice (1-7): ");

        var choice = Console.ReadLine();

        int concurrentUsers, durationSeconds;

        switch (choice)
        {
            case "1":
                concurrentUsers = 10;
                durationSeconds = 60;
                break;
            case "2":
                concurrentUsers = 50;
                durationSeconds = 120;
                break;
            case "3":
                concurrentUsers = 100;
                durationSeconds = 180;
                break;
            case "4":
                concurrentUsers = 200;
                durationSeconds = 300;
                break;
            case "5":
                concurrentUsers = 500;
                durationSeconds = 300;
                break;
            case "6":
                concurrentUsers = 1000;
                durationSeconds = 600;
                break;
            case "7":
                Console.Write("Enter concurrent users: ");
                concurrentUsers = int.Parse(Console.ReadLine() ?? "50");
                Console.Write("Enter duration in seconds: ");
                durationSeconds = int.Parse(Console.ReadLine() ?? "60");
                break;
            default:
                Console.WriteLine("Invalid choice. Using Light Load Test.");
                concurrentUsers = 50;
                durationSeconds = 120;
                break;
        }

        Console.WriteLine($"\n🚀 Starting Load Test:");
        Console.WriteLine($"   Concurrent Users: {concurrentUsers}");
        Console.WriteLine($"   Duration: {durationSeconds}s");
        Console.WriteLine($"   Start Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("\nPress Ctrl+C to stop early...\n");

        await RunLoadTest(concurrentUsers, durationSeconds);

        PrintResults();
    }

    static async Task RunLoadTest(int concurrentUsers, int durationSeconds)
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));

        var stopwatch = Stopwatch.StartNew();
        var tasks = new List<Task>();

        // Progress reporter task
        var progressTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(5000, CancellationToken.None);
                var elapsed = stopwatch.Elapsed;
                var rps = _totalRequests / elapsed.TotalSeconds;
                Console.WriteLine($"[{elapsed:mm\\:ss}] Requests: {_totalRequests} | Success: {_successfulRequests} | Failed: {_failedRequests} | RPS: {rps:F1}");
            }
        }, CancellationToken.None);

        // User simulation tasks
        for (int i = 0; i < concurrentUsers; i++)
        {
            var userId = i;
            tasks.Add(Task.Run(async () => await SimulateUser(userId, cts.Token)));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        Console.WriteLine($"\n✅ Load test completed in {stopwatch.Elapsed:mm\\:ss}");
    }

    static async Task SimulateUser(int userId, CancellationToken ct)
    {
        var random = new Random(userId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Scenario: Create -> Pull -> Toggle -> Rename -> Delete
                var todoId = $"todo-{userId}-{Guid.NewGuid():N}";

                // 1. Create Todo
                await MeasureRequest(async () =>
                {
                    var createEvent = new TodoEvent
                    {
                        EventId = Guid.NewGuid().ToString(),
                        Type = "TODO_CREATED",
                        TodoId = todoId,
                        Payload = JsonSerializer.SerializeToElement(new
                        {
                            title = $"Load Test {userId}",
                            priority = "MEDIUM",
                            dayKey = "2026-03-14"
                        }),
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Synced = 0
                    };

                    var request = new SyncPushRequest { Events = new List<TodoEvent> { createEvent } };
                    var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/sync/push", request, ct);
                    return response.IsSuccessStatusCode;
                }, ct);

                await Task.Delay(random.Next(100, 500), ct);

                // 2. Pull Todos
                await MeasureRequest(async () =>
                {
                    var response = await _httpClient.GetAsync($"{_baseUrl}/api/sync/pull?since=0", ct);
                    return response.IsSuccessStatusCode;
                }, ct);

                await Task.Delay(random.Next(100, 500), ct);

                // 3. Toggle Todo (50% chance)
                if (random.Next(0, 2) == 0)
                {
                    await MeasureRequest(async () =>
                    {
                        var toggleEvent = new TodoEvent
                        {
                            EventId = Guid.NewGuid().ToString(),
                            Type = "TODO_TOGGLED",
                            TodoId = todoId,
                            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Synced = 0
                        };

                        var request = new SyncPushRequest { Events = new List<TodoEvent> { toggleEvent } };
                        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/sync/push", request, ct);
                        return response.IsSuccessStatusCode;
                    }, ct);
                }

                await Task.Delay(random.Next(200, 800), ct);

                // 4. Delete Todo (30% chance)
                if (random.Next(0, 10) < 3)
                {
                    await MeasureRequest(async () =>
                    {
                        var deleteEvent = new TodoEvent
                        {
                            EventId = Guid.NewGuid().ToString(),
                            Type = "TODO_DELETED",
                            TodoId = todoId,
                            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Synced = 0
                        };

                        var request = new SyncPushRequest { Events = new List<TodoEvent> { deleteEvent } };
                        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/sync/push", request, ct);
                        return response.IsSuccessStatusCode;
                    }, ct);
                }

                await Task.Delay(random.Next(500, 2000), ct);
            }
            catch (OperationCanceledException)
            {
                // Test cancelled
                break;
            }
            catch (Exception)
            {
                // Ignore individual errors
            }
        }
    }

    static async Task MeasureRequest(Func<Task<bool>> requestFunc, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var success = await requestFunc();
            sw.Stop();

            lock (_lock)
            {
                _totalRequests++;
                _latencies.Add(sw.Elapsed.TotalMilliseconds);

                if (success)
                    _successfulRequests++;
                else
                    _failedRequests++;
            }
        }
        catch (Exception)
        {
            sw.Stop();
            lock (_lock)
            {
                _totalRequests++;
                _failedRequests++;
            }
        }
    }

    static void PrintResults()
    {
        Console.WriteLine("\n=================================================");
        Console.WriteLine("  LOAD TEST RESULTS");
        Console.WriteLine("=================================================");

        Console.WriteLine($"\n📊 Request Statistics:");
        Console.WriteLine($"   Total Requests:      {_totalRequests:N0}");
        Console.WriteLine($"   ✅ Successful:       {_successfulRequests:N0} ({(_totalRequests > 0 ? (double)_successfulRequests / _totalRequests * 100 : 0):F2}%)");
        Console.WriteLine($"   ❌ Failed:           {_failedRequests:N0} ({(_totalRequests > 0 ? (double)_failedRequests / _totalRequests * 100 : 0):F2}%)");

        if (_latencies.Count > 0)
        {
            _latencies.Sort();

            var avg = _latencies.Average();
            var min = _latencies.Min();
            var max = _latencies.Max();
            var p50 = GetPercentile(_latencies, 0.50);
            var p95 = GetPercentile(_latencies, 0.95);
            var p99 = GetPercentile(_latencies, 0.99);

            Console.WriteLine($"\n⏱️  Latency (milliseconds):");
            Console.WriteLine($"   Average:    {avg:F2} ms");
            Console.WriteLine($"   Min:        {min:F2} ms");
            Console.WriteLine($"   Max:        {max:F2} ms");
            Console.WriteLine($"   P50:        {p50:F2} ms");
            Console.WriteLine($"   P95:        {p95:F2} ms");
            Console.WriteLine($"   P99:        {p99:F2} ms");

            Console.WriteLine($"\n📈 Performance Assessment:");

            if (p95 < 1000 && (double)_failedRequests / _totalRequests < 0.01)
            {
                Console.WriteLine("   ✅ EXCELLENT - System performing well");
            }
            else if (p95 < 2000 && (double)_failedRequests / _totalRequests < 0.05)
            {
                Console.WriteLine("   ✅ GOOD - System stable with acceptable latency");
            }
            else if (p95 < 5000 && (double)_failedRequests / _totalRequests < 0.10)
            {
                Console.WriteLine("   ⚠️  DEGRADED - System under stress");
            }
            else
            {
                Console.WriteLine("   ❌ CRITICAL - System overloaded or failing");
            }

            Console.WriteLine($"\n💡 Recommendations:");
            if (p95 > 2000)
            {
                Console.WriteLine("   - Consider adding caching layer (Redis)");
                Console.WriteLine("   - Optimize database queries");
                Console.WriteLine("   - Reduce lock contention in EventStoreService");
            }
            if ((double)_failedRequests / _totalRequests > 0.05)
            {
                Console.WriteLine("   - Investigate error logs");
                Console.WriteLine("   - Add rate limiting");
                Console.WriteLine("   - Scale horizontally (multiple instances)");
            }
            if (_totalRequests > 10000)
            {
                Console.WriteLine("   - Migrate from file-based snapshot to database");
                Console.WriteLine("   - Implement connection pooling");
            }
        }

        Console.WriteLine("\n=================================================\n");
    }

    static double GetPercentile(List<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;

        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        if (index < 0) index = 0;
        if (index >= sortedValues.Count) index = sortedValues.Count - 1;

        return sortedValues[index];
    }
}
