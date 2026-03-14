# Load Testing Scripts for TodoSync API

This directory contains k6 load testing scripts to benchmark the TodoSync API performance and capacity.

## Prerequisites

Install k6:
- **Windows**: `choco install k6` or download from https://k6.io/docs/getting-started/installation/
- **macOS**: `brew install k6`
- **Linux**: `sudo snap install k6` or see https://k6.io/docs/getting-started/installation/

## Test Scripts

### 1. Smoke Test (`smoke-test.js`)
**Purpose**: Basic functionality verification with minimal load

**Configuration**:
- 10 virtual users
- 1 minute duration
- Validates core endpoints work correctly

**Run**:
```bash
k6 run smoke-test.js
```

### 2. Stress Test (`stress-test.js`)
**Purpose**: Find the breaking point and maximum capacity

**Configuration**:
- Ramps from 0 → 50 → 100 → 200 → 500 → 1000 users
- Total duration: ~11 minutes
- Tests all CRUD operations + pagination

**Run**:
```bash
k6 run stress-test.js
```

**Custom URL**:
```bash
k6 run stress-test.js -e BASE_URL=http://your-server:3000
```

**With HTML report**:
```bash
k6 run stress-test.js --out json=results.json
```

### 3. Spike Test (`spike-test.js`)
**Purpose**: Test behavior under sudden traffic surges

**Configuration**:
- Sudden spike from 100 → 2000 users in 1 minute
- Maintains 2000 users for 3 minutes
- Tests recovery after spike

**Run**:
```bash
k6 run spike-test.js
```

### 4. Soak Test (`soak-test.js`)
**Purpose**: Detect memory leaks and performance degradation over time

**Configuration**:
- 300 concurrent users
- 30 minutes duration
- Monitors stability under prolonged load

**Run**:
```bash
k6 run soak-test.js
```

## Interpreting Results

### Key Metrics

**http_req_duration**: Request latency
- `p(95)`: 95th percentile - 95% of requests complete within this time
- `p(99)`: 99th percentile - 99% of requests complete within this time
- Target: p95 < 2s, p99 < 5s

**http_req_failed**: Error rate
- Percentage of failed HTTP requests
- Target: < 5%

**Custom Metrics**:
- `push_duration`: Time to push events
- `pull_duration`: Time to pull todos
- `todos_created`: Total todos created
- `errors`: Custom error tracking

### Success Criteria

✅ **Passing Test**:
```
✓ http_req_duration..........: avg=250ms p95=800ms p99=1.2s
✓ http_req_failed............: 0.5%
✓ errors.....................: 0.2%
```

❌ **Failing Test**:
```
✗ http_req_duration..........: avg=5s p95=12s p99=30s
✗ http_req_failed............: 15%
✗ errors.....................: 25%
```

## Example Commands

### Run full stress test with detailed output
```bash
k6 run stress-test.js --out json=results.json --summary-export=summary.json
```

### Run smoke test in quiet mode
```bash
k6 run smoke-test.js --quiet
```

### Run with specific VUs and duration
```bash
k6 run stress-test.js --vus 500 --duration 5m
```

### Run with cloud output (requires k6 cloud account)
```bash
k6 run stress-test.js --out cloud
```

## Performance Baseline (Expected Results)

Based on current architecture (in-memory + file snapshot):

| Metric | Expected Value |
|--------|----------------|
| Max concurrent users | 500-1000 |
| Max requests/sec | 500-1000 |
| P95 latency @ 100 users | 200-500ms |
| P95 latency @ 500 users | 1-3s |
| P95 latency @ 1000 users | 3-10s (degraded) |
| Error rate @ 100 users | < 1% |
| Error rate @ 1000 users | 5-20% |

## Monitoring During Tests

While tests run, monitor:
1. **CPU usage**: `top` or Task Manager
2. **Memory usage**: Should stay relatively stable
3. **Disk I/O**: File snapshot writes
4. **Network**: Request/response throughput

### Windows Monitoring
```powershell
# Monitor .NET process
Get-Process | Where-Object {$_.ProcessName -like "*TodoSync*"} | Format-Table ProcessName,CPU,WorkingSet64
```

### Linux/Mac Monitoring
```bash
# Monitor CPU and memory
top -p $(pgrep -f TodoSync.Api)

# Monitor file operations
lsof -p $(pgrep -f TodoSync.Api) | grep state.json
```

## Troubleshooting

### "connection refused" errors
- Ensure API is running: `dotnet run --project ../TodoSync.Api`
- Check correct URL: default is `http://localhost:3000`

### High error rates
- Check API logs for exceptions
- Verify file system isn't full (state.json writes)
- Monitor memory usage for OOM

### Timeouts
- Increase timeout in k6: `http.get(url, { timeout: '60s' })`
- Check for lock contention (SemaphoreSlim)

## Next Steps

After identifying capacity limits:
1. Review BACKEND_UPGRADE_BLUEPRINT.md
2. Implement database persistence (Phase 1)
3. Add caching layer (Redis)
4. Implement horizontal scaling
5. Re-run tests to measure improvements
