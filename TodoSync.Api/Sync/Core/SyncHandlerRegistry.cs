using TodoSync.Api.Sync.Contracts;

namespace TodoSync.Api.Sync.Core;

public sealed class SyncHandlerRegistry
{
    private readonly IReadOnlyDictionary<(string EntityType, int SchemaVersion), ISyncMutationHandler> _handlers;

    public SyncHandlerRegistry(IEnumerable<ISyncMutationHandler> handlers)
    {
        var map = new Dictionary<(string, int), ISyncMutationHandler>();

        foreach (var handler in handlers)
        {
            var key = (Normalize(handler.EntityType), handler.SchemaVersion);
            if (!map.TryAdd(key, handler))
            {
                throw new InvalidOperationException(
                    $"Duplicate sync handler registration for '{key.Item1}' schema v{key.Item2}.");
            }
        }

        _handlers = map;
    }

    public IReadOnlyCollection<string> EntityTypes => _handlers.Keys
        .Select(x => x.EntityType)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    public ISyncMutationHandler Resolve(string entityType, int schemaVersion)
    {
        var key = (Normalize(entityType), schemaVersion);
        if (_handlers.TryGetValue(key, out var handler)) return handler;

        throw new SyncValidationException(
            "SYNC_HANDLER_NOT_FOUND",
            $"No sync handler is registered for '{key.Item1}' schema v{schemaVersion}.");
    }

    public IReadOnlyList<string> NormalizeAndValidateScope(IEnumerable<string>? entityTypes)
    {
        var normalized = (entityTypes ?? ["todo"])
            .Select(Normalize)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0 || normalized.Length > 20)
        {
            throw new SyncValidationException("SYNC_SCOPE_INVALID", "The sync scope must contain 1 to 20 entity types.");
        }

        var supported = EntityTypes.ToHashSet(StringComparer.Ordinal);
        var unknown = normalized.FirstOrDefault(x => !supported.Contains(x));
        if (unknown is not null)
        {
            throw new SyncValidationException("SYNC_SCOPE_UNSUPPORTED", $"Entity type '{unknown}' is not supported.");
        }

        return normalized;
    }

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
