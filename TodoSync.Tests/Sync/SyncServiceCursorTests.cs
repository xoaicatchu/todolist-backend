using System.Diagnostics.Metrics;
using FluentAssertions;
using TodoSync.Api.Data.Entities;
using TodoSync.Api.Data.Repositories;
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Sync.Core;
using TodoSync.Api.Sync.Observability;
using Xunit;

namespace TodoSync.Tests.Sync;

public sealed class SyncServiceCursorTests
{
    [Fact]
    public async Task PullAsync_CursorKeepsStableUpperBound()
    {
        var repository = new InMemoryChangeRepository();
        repository.Add(1);
        repository.Add(2);
        repository.Add(3);
        var service = CreateService(repository);

        var first = await service.PullAsync(null, 2, null, ["todo"]);
        first.Changes.Select(x => x.ChangeId).Should().Equal(
            repository.Changes[0].ChangeId,
            repository.Changes[1].ChangeId);
        first.HasMore.Should().BeTrue();
        first.NextCursor.Should().NotBeNull();
        first.ServerWatermark.Should().Be(repository.Changes[2].ChangeId);

        // A concurrent write must not leak into the already-open cursor window.
        repository.Add(4);
        var second = await service.PullAsync(
            first.ServerWatermark,
            2,
            first.NextCursor,
            ["todo"]);

        second.Changes.Select(x => x.ChangeId).Should().Equal(repository.Changes[2].ChangeId);
        second.HasMore.Should().BeFalse();
        second.ServerWatermark.Should().Be(first.ServerWatermark);

        var nextWindow = await service.PullAsync(
            first.ServerWatermark,
            2,
            null,
            ["todo"]);
        nextWindow.Changes.Select(x => x.ChangeId).Should().Equal(repository.Changes[3].ChangeId);
    }

    private static SyncService CreateService(ISyncChangeRepository repository) =>
        new(
            repository,
            new SyncHandlerRegistry([new NoOpHandler()]),
            new SyncCursorCodec(),
            null!,
            new SyncMetrics(new TestMeterFactory()));

    private sealed class NoOpHandler : ISyncMutationHandler
    {
        public string EntityType => "todo";
        public int SchemaVersion => 1;

        public SyncValidationResult Validate(MutationEnvelope mutation) => SyncValidationResult.Success;

        public Task<SyncHandlerBatchResult> HandleBatchAsync(
            string tenantId,
            IReadOnlyList<MutationEnvelope> mutations,
            CancellationToken ct = default) =>
            Task.FromResult(new SyncHandlerBatchResult(mutations.Count, 0));
    }

    private sealed class InMemoryChangeRepository : ISyncChangeRepository
    {
        public List<SyncChangeEntity> Changes { get; } = [];

        public void Add(long sequence)
        {
            Changes.Add(new SyncChangeEntity
            {
                ChangeSequence = sequence,
                ChangeId = Guid.CreateVersion7(),
                TenantId = "default",
                EntityType = "todo",
                EntityId = $"todo-{sequence}",
                Operation = "upsert",
                PayloadJson = "{}",
                CreatedAt = sequence,
                EventId = $"event-{sequence}"
            });
        }

        public Task<List<SyncChangeEntity>> GetChangesSinceAsync(
            Guid? sinceChangeId,
            int limit,
            string tenantId = "default",
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Guid?> GetLatestChangeIdAsync(
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.FromResult<Guid?>(Changes.LastOrDefault()?.ChangeId);

        public Task<List<SyncChangeEntity>> GetChangesWindowAsync(
            long afterSequence,
            long upperBoundSequence,
            int limit,
            IReadOnlyCollection<string> entityTypes,
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.FromResult(Changes
                .Where(x =>
                    x.ChangeSequence > afterSequence &&
                    x.ChangeSequence <= upperBoundSequence &&
                    entityTypes.Contains(x.EntityType))
                .OrderBy(x => x.ChangeSequence)
                .Take(limit)
                .ToList());

        public Task<SyncChangePosition?> GetLatestPositionAsync(
            IReadOnlyCollection<string> entityTypes,
            string tenantId = "default",
            CancellationToken ct = default)
        {
            var latest = Changes
                .Where(x => entityTypes.Contains(x.EntityType))
                .MaxBy(x => x.ChangeSequence);
            return Task.FromResult(latest is null
                ? null
                : new SyncChangePosition(latest.ChangeSequence, latest.ChangeId));
        }

        public Task<long?> GetSequenceAsync(
            Guid changeId,
            IReadOnlyCollection<string> entityTypes,
            string tenantId = "default",
            CancellationToken ct = default) =>
            Task.FromResult(Changes
                .Where(x => x.ChangeId == changeId && entityTypes.Contains(x.EntityType))
                .Select(x => (long?)x.ChangeSequence)
                .SingleOrDefault());

        public Task<SyncChangeEntity> CreateAsync(
            SyncChangeEntity change,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<SyncChangeEntity>> CreateBatchAsync(
            List<SyncChangeEntity> changes,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }
}
