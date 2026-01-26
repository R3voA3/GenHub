using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Models.Parsers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GenHub.Features.Content.Services;

/// <summary>
/// Default implementation of <see cref="IContentCacheService"/> using an in-memory cache.
/// </summary>
public sealed class ContentCacheService(ILogger<ContentCacheService> logger, IMemoryCache cache) : IContentCacheService
{
    private readonly TimeSpan _defaultTtl = TimeSpan.FromHours(1);

    /// <inheritdoc/>
    public Task<ParsedWebPage?> GetAsync(string cacheKey, CancellationToken ct = default)
    {
        if (cache.TryGetValue(cacheKey, out ParsedWebPage? data))
        {
            logger.LogDebug("Cache hit for {CacheKey}", cacheKey);
            return Task.FromResult(data);
        }

        logger.LogDebug("Cache miss for {CacheKey}", cacheKey);
        return Task.FromResult<ParsedWebPage?>(null);
    }

    /// <inheritdoc/>
    public Task SetAsync(string cacheKey, ParsedWebPage data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiresAt = DateTime.UtcNow + (ttl ?? _defaultTtl);

        using var entry = cache.CreateEntry(cacheKey);
        entry.Value = data;
        entry.AbsoluteExpiration = expiresAt;

        logger.LogDebug("Cached {CacheKey} until {ExpiresAt}", cacheKey, expiresAt);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public bool HasValidCache(string cacheKey)
    {
        // IMemoryCache doesn't expose a way to check without retrieving,
        // but TryGetValue works for this purpose and handles expiration.
        return cache.TryGetValue(cacheKey, out _);
    }

    /// <inheritdoc/>
    public void Invalidate(string cacheKey)
    {
        cache.Remove(cacheKey);
        logger.LogDebug("Invalidated cache for {CacheKey}", cacheKey);
    }

    /// <summary>
    /// Clears all cached data.
    /// Note: IMemoryCache does not support clearing all entries.
    /// This implementation is a no-op as MemoryCache handles eviction automatically.
    /// </summary>
    public void ClearAll()
    {
        // Compact 100% to force eviction of everything that can be evicted
        if (cache is MemoryCache memoryCache)
        {
            memoryCache.Compact(1.0);
            logger.LogInformation("Compacted content cache");
        }
        else
        {
            logger.LogWarning("ClearAll called but cache is not strictly MemoryCache, skipping compaction");
        }
    }
}
