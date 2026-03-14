# Running Tests

## Quick Start

### 1. Run Unit & Integration Tests
```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test TodoSync.Tests/TodoSync.Tests.csproj

# Run with detailed output
dotnet test --verbosity detailed
```

### 2. Run Load Tests

**Prerequisites**: Start the API server first
```bash
# Terminal 1: Start API
cd TodoSync.Api
dotnet run

# Terminal 2: Run load test
cd TodoSync.LoadTest
dotnet run
```

**Available Test Scenarios**:
1. Smoke Test (10 users, 1 min) - Quick verification
2. Light Load (50 users, 2 min) - Normal usage simulation
3. Medium Load (100 users, 3 min) - Peak hour simulation
4. Heavy Load (200 users, 5 min) - Stress boundary
5. Stress Test (500 users, 5 min) - Breaking point
6. Extreme (1000 users, 10 min) - Chaos test
7. Custom - Configure your own parameters

**Example Output**:
```
🚀 Starting Load Test:
   Concurrent Users: 50
   Duration: 120s

[00:05] Requests: 302 | Success: 302 | Failed: 0 | RPS: 60.4
[00:10] Requests: 601 | Success: 601 | Failed: 0 | RPS: 60.0

✅ Load test completed in 02:00

=================================================
  LOAD TEST RESULTS
=================================================
📊 Request Statistics:
   Total Requests:      6,692
   ✅ Successful:       6,692 (100.00%)
   P95 Latency:         171.32 ms

📈 Performance Assessment:
   ✅ EXCELLENT - System performing well
```

### 3. Run k6 Tests (Alternative)

**Install k6**:
- Windows: `choco install k6`
- macOS: `brew install k6`
- Linux: `snap install k6`

**Run Tests**:
```bash
# Smoke test
k6 run LoadTests/smoke-test.js

# Full stress test
k6 run LoadTests/stress-test.js

# Spike test
k6 run LoadTests/spike-test.js

# Custom URL
k6 run LoadTests/stress-test.js -e BASE_URL=http://your-server:3000
```

## Test Results Summary

See [TEST_REPORT.md](TEST_REPORT.md) for detailed analysis.

**Key Findings**:
- ✅ Excellent performance with 50 concurrent users (P95: 171ms)
- ⚠️ Degraded with 200 concurrent users (P95: 5,878ms)
- ❌ **Recommended limit**: < 100 concurrent users
- ❌ **Not production-ready** for > 100 users without upgrades

## Next Steps

For production deployment, see [BACKEND_UPGRADE_BLUEPRINT.md](BACKEND_UPGRADE_BLUEPRINT.md).
