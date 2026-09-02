using System.Text.Json;

namespace TodoSync.Api.Sync.Contracts;

public sealed class MutationEnvelope
{
    public required string MutationId { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string MutationType { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public long OccurredAt { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class ChangeEnvelope
{
    public Guid ChangeId { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string Op { get; set; }
    public long? EntityVersion { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public long OccurredAt { get; set; }
    public string? SourceMutationId { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class GenericSyncPushResponse
{
    public string Status { get; set; } = "queued";
    public List<string> AcceptedMutationIds { get; set; } = [];
}

public sealed class SyncPullV2Response
{
    public List<ChangeEnvelope> Changes { get; set; } = [];
    public Guid? ServerWatermark { get; set; }
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public sealed class GenericSyncPushMessage
{
    public string TenantId { get; set; } = "default";
    public List<MutationEnvelope> Mutations { get; set; } = [];
}

public sealed record SyncHandlerBatchResult(int AppliedMutations, int DuplicateMutations);

public sealed record SyncBatchResult(
    Guid? ServerWatermark,
    IReadOnlyCollection<string> EntityTypes,
    int AppliedMutations,
    int DuplicateMutations);

public sealed record SyncValidationResult(bool IsValid, string? Code = null, string? Message = null)
{
    public static SyncValidationResult Success { get; } = new(true);

    public static SyncValidationResult Failure(string code, string message) => new(false, code, message);
}

public sealed class SyncValidationException : Exception
{
    public SyncValidationException(string code, string message, string? mutationId = null)
        : base(message)
    {
        Code = code;
        MutationId = mutationId;
    }

    public string Code { get; }
    public string? MutationId { get; }
}
