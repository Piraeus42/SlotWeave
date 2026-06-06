using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace SlotWeave;

/// <summary>
/// Disk-backed cache for patched script results.
/// Key = SHA256(original_source + mod_fingerprints + SlotWeave_version).
/// Each mod fingerprint = "Id:Version:ContentHash" where ContentHash is
/// SHA256 of the mod's assembly + pack + manifest files.
/// Eliminates re-running the SourceModder pipeline on every reload.
/// </summary>
internal class CacheManager
{
    private readonly string cacheDir;
    private readonly ILogger logger = SlotWeave.Logger.ForContext<CacheManager>();
    private readonly Dictionary<string, string> memoryCache = new();

    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    private readonly bool disabled;

    public CacheManager()
    {
        this.disabled = Environment.GetEnvironmentVariable("GDWEAVE_NO_CACHE") is not null;
        this.cacheDir = Path.Combine(SlotWeave.SlotWeaveDir, "cache");

        if (this.disabled) return;

        // Auto-invalidate stale cache when SlotWeave assembly changes
        // Uses SHA256 of SlotWeave.dll (not just the version string) so
        // hotfix / dev builds with the same version number still trigger
        // a full cache clear.
        var versionFile = Path.Combine(this.cacheDir, ".version");
        var currentHash = SlotWeave.SelfHash;
        if (File.Exists(versionFile))
        {
            var stored = File.ReadAllText(versionFile).Trim();
            if (stored != currentHash)
            {
                logger.Information("SlotWeave changed ({Old} -> {New}), clearing cache",
                    stored[..Math.Min(12, stored.Length)], currentHash[..12]);
                Clear();
            }
        }
        Directory.CreateDirectory(this.cacheDir);
        File.WriteAllText(versionFile, currentHash);
    }

    /// <summary>Compute the cache key from source + mod identity + SlotWeave version.</summary>
    /// <remarks>
    /// Sorted by mod ID to ensure deterministic ordering. Each mod contributes its
    /// version string AND a content hash of its files (assembly + pack + manifest).
    /// This guarantees the cache is invalidated when any mod file changes, even if
    /// the developer forgets to bump the manifest version.
    /// </remarks>
    private static string ComputeKey(string source, List<(string Id, string Version, string ContentHash)> mods)
    {
        var sorted = mods.OrderBy(m => m.Id).ToList();
        var modSegment = string.Join(",",
            sorted.Select(m => $"{m.Id}:{m.Version}:{m.ContentHash}"));
        var input = source + "\0" + modSegment + "\0"
                    + SlotWeave.Version + "\0" + SlotWeave.SelfHash;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Try to retrieve a cached result. Returns null on miss.</summary>
    public string? Lookup(string source, List<(string Id, string Version, string ContentHash)> mods)
    {
        if (this.disabled) return null;
        var key = ComputeKey(source, mods);

        if (this.memoryCache.TryGetValue(key, out var cached))
        {
            this.HitCount++;
            this.logger.Debug("Cache hit (mem): {Key}", key[..12]);
            EventBus.Publish(new ModEvents.CacheHit(cached[..Math.Min(80, cached.Length)]));
            return cached;
        }

        var file = Path.Combine(this.cacheDir, key);
        if (File.Exists(file))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file));
                if (entry != null)
                {
                    this.memoryCache[key] = entry.Patched;
                    this.HitCount++;
                    this.logger.Debug("Cache hit (disk): {Key}", key[..12]);
                    EventBus.Publish(new ModEvents.CacheHit(key[..12]));
                    return entry.Patched;
                }
            }
            catch (Exception e)
            {
                this.logger.Warning(e, "Corrupt cache entry {Key} — deleting", key[..12]);
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }

        this.MissCount++;
        return null;
    }

    /// <summary>Store a patched result in the cache.</summary>
    public void Store(string source, List<(string Id, string Version, string ContentHash)> mods, string patched)
    {
        if (this.disabled) return;
        var key = ComputeKey(source, mods);
        this.memoryCache[key] = patched;

        var file = Path.Combine(this.cacheDir, key);
        try
        {
            var entry = new CacheEntry(key, patched);
            File.WriteAllText(file, JsonSerializer.Serialize(entry));
            this.logger.Debug("Cache stored: {Key}", key[..12]);
            EventBus.Publish(new ModEvents.CacheStored(key[..12]));
        }
        catch (Exception e)
        {
            this.logger.Warning(e, "Failed to write cache entry {Key}", key[..12]);
        }
    }

    public void Clear()
    {
        this.memoryCache.Clear();
        try
        {
            foreach (var file in Directory.GetFiles(this.cacheDir))
                File.Delete(file);
        }
        catch (Exception e)
        {
            this.logger.Warning(e, "Failed to clear cache");
        }
    }

    private class CacheEntry
    {
        public string Key { get; set; }
        public string Patched { get; set; }

        public CacheEntry() { Key = Patched = ""; }
        public CacheEntry(string key, string patched) { Key = key; Patched = patched; }
    }
}
