using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TodoSync.Api.Sync.Contracts;

namespace TodoSync.Api.Sync.Core;

public sealed record SyncCursor(
    int Version,
    string TenantId,
    string Scope,
    long AfterSequence,
    long UpperBoundSequence,
    Guid UpperBoundChangeId);

public sealed class SyncCursorCodec
{
    private const int CurrentVersion = 1;

    public string Encode(
        string tenantId,
        IReadOnlyList<string> entityTypes,
        long afterSequence,
        long upperBoundSequence,
        Guid upperBoundChangeId)
    {
        var cursor = new SyncCursor(
            CurrentVersion,
            tenantId,
            Scope(entityTypes),
            afterSequence,
            upperBoundSequence,
            upperBoundChangeId);
        return WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(cursor));
    }

    public SyncCursor Decode(
        string value,
        string expectedTenantId,
        IReadOnlyList<string> expectedEntityTypes)
    {
        try
        {
            var cursor = JsonSerializer.Deserialize<SyncCursor>(WebEncoders.Base64UrlDecode(value));
            if (cursor is null ||
                cursor.Version != CurrentVersion ||
                !string.Equals(cursor.TenantId, expectedTenantId, StringComparison.Ordinal) ||
                !string.Equals(cursor.Scope, Scope(expectedEntityTypes), StringComparison.Ordinal) ||
                cursor.AfterSequence < 0 ||
                cursor.UpperBoundSequence <= 0 ||
                cursor.UpperBoundChangeId == Guid.Empty ||
                cursor.AfterSequence > cursor.UpperBoundSequence)
            {
                throw new SyncValidationException("SYNC_CURSOR_INVALID", "The sync cursor is invalid for this tenant or scope.");
            }

            return cursor;
        }
        catch (SyncValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new SyncValidationException("SYNC_CURSOR_INVALID", "The sync cursor is malformed.");
        }
    }

    private static string Scope(IReadOnlyList<string> entityTypes) => string.Join(',', entityTypes);
}
