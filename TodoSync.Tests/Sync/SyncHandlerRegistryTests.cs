using FluentAssertions;
using TodoSync.Api.Sync.Contracts;
using TodoSync.Api.Sync.Core;
using Xunit;

namespace TodoSync.Tests.Sync;

public sealed class SyncHandlerRegistryTests
{
    [Fact]
    public void Resolve_AllowsSecondEntityWithoutCoreChanges()
    {
        var registry = new SyncHandlerRegistry([new FakeHandler("todo"), new FakeHandler("note")]);

        registry.Resolve("NOTE", 1).EntityType.Should().Be("note");
        registry.EntityTypes.Should().BeEquivalentTo(["note", "todo"]);
    }

    [Fact]
    public void DuplicateRegistration_ShouldFailAtStartup()
    {
        var action = () => new SyncHandlerRegistry([new FakeHandler("todo"), new FakeHandler("TODO")]);

        action.Should().Throw<InvalidOperationException>();
    }

    private sealed class FakeHandler(string entityType) : ISyncMutationHandler
    {
        public string EntityType { get; } = entityType;
        public int SchemaVersion => 1;

        public SyncValidationResult Validate(MutationEnvelope mutation) => SyncValidationResult.Success;

        public Task<SyncHandlerBatchResult> HandleBatchAsync(
            string tenantId,
            IReadOnlyList<MutationEnvelope> mutations,
            CancellationToken ct = default) =>
            Task.FromResult(new SyncHandlerBatchResult(mutations.Count, 0));
    }
}
