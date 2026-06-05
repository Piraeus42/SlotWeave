using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace SlotWeave;

/// <summary>
/// Disk-backed cache for patched script results.
/// Key = SHA256(original_source + sorted_mod_ids + sorted_mod_versions).
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

        // Auto-invalidate stale cache when SlotWeave version changes
        var versionFile = Path.Combine(this.cacheDir, ".version");
        var currentVersion = SlotWeave.Version;
        if (File.Exists(versionFile))
        {
            var storedVersion = File.ReadAllText(versionFile).Trim();
            if (storedVersion != currentVersion)
            {
                logger.Information("SlotWeave version changed ({Old} -> {New}), clearing cache",
                    storedVersion, currentVersion);
                Clear();
            }
        }
        Directory.CreateDirectory(this.cacheDir);
        File.WriteAllText(versionFile, currentVersion);
    }

    /// <summary>Compute the cache key from source + mod identity + SlotWeave version.</summary>
    private static string ComputeKey(string source, List<(string Id, string Version)> mods)
    {
        var ids = mods.Select(m => m.Id).OrderBy(x => x);
        var versions = mods.Select(m => m.Version).OrderBy(x => x);
        var input = source + "\0" + string.Join(",", ids) + "\0" + string.Join(",", versions)
                    + "\0" + SlotWeave.Version;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Try to retrieve a cached result. Returns null on miss.</summary>
    public string? Lookup(string source, List<(string Id, string Version)> mods)
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
                this.logger.Warning(e, "Corrupt cache entry {Key}", key[..12]);
            }
        }

        this.MissCount++;
        return null;
    }

    /// <summary>Store a patched result in the cache.</summary>
    public void Store(string source, List<(string Id, string Version)> mods, string patched)
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
