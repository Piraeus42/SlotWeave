using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlotWeave.GameState;
using SlotWeave.Modding;
using SlotWeave.Scripting;

using Serilog;

namespace SlotWeave;

// ReSharper disable InconsistentNaming
internal class SlotWeave {
    public static readonly Assembly Assembly = Assembly.GetExecutingAssembly();
    public static readonly string Version = Assembly.GetName().Version!.ToString();

    /// <summary>
    /// SHA256 of SlotWeave.dll — used in cache keys and cache invalidation.
    /// Any change to the SlotWeave assembly triggers a full cache clear, even if
    /// the assembly version string wasn't bumped (e.g. hotfix / dev builds).
    /// </summary>
    public static readonly string SelfHash = ComputeSelfHash();

    private static string ComputeSelfHash()
    {
        try
        {
            var dllPath = Assembly.Location;
            if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
                return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dllPath))).ToLowerInvariant();
        }
        catch { /* fall through to fallback */ }
        // Fallback: if we can't hash the file, use the version string so
        // the cache still functions (version-bump invalidation still works).
        return Version;
    }

    public static string GameDir = null!;
    public static string SlotWeaveDir = null!;

    public static ILogger Logger = null!;
    public static ModLoader ModLoader = null!;
    public static CacheManager Cache = null!;
    public static PatchManager Patches = null!;
    public static Interop Interop = null!;
    public static Hooks Hooks = null!;
    public static GameStateBus GameStateBus = null!;

    public static JsonSerializerOptions JsonSerializerOptions = new() {
        WriteIndented = true,
        Converters = {new JsonStringEnumConverter()}
    };

    public delegate void MainDelegate();

    public static void Main() {
        try {
            Init();
        } catch (Exception e) {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (Logger is not null) {
                Logger.Error(e, "SlotWeave failed to initialize");
            } else {
                Console.WriteLine($"SlotWeave failed to initialize: {e.Message}");
            }
        }
    }

    private static void Init() {
        ConsoleFixer.Init();

        GameDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        SlotWeaveDir = Environment.GetEnvironmentVariable("GDWEAVE_FOLDER_OVERRIDE") ?? Path.Combine(GameDir, "SlotWeave");

        var logPath = Path.Combine(SlotWeaveDir, "SlotWeave.log");
        if (File.Exists(logPath)) File.Delete(logPath);

        var config = new LoggerConfiguration()
            .WriteTo.File(logPath)
            .WriteTo.Console();

        if (Environment.GetEnvironmentVariable("GDWEAVE_DEBUG") is not null) {
            config.MinimumLevel.Verbose();
        } else {
            config.MinimumLevel.Information();
        }

        Logger = config.CreateLogger();
        Log.Logger = Logger;

        const string github = "https://github.com/NotNite/SlotWeave";
        Logger.Information("This is SlotWeave {Version} - {GitHub}", Version, github);

        Patches = new PatchManager();
        Interop = new Interop();

        // Create GameStateBus before ModLoader so mods can register readers during OnLoad
        GameStateBus = new GameStateBus(Interop);
        ModLoader = new ModLoader(Patches, GameStateBus);
        Cache = new CacheManager();

        List<ISourceMod> sourceMods = [
            new PatchSourceMod(Patches),
            ..ModLoader.SourceMods.OrderByDescending(m => m.Priority),
        ];

        var scriptModder = new SourceModder(sourceMods);
        Hooks = new Hooks(scriptModder, Interop, Cache, ModLoader.ActiveModVersions);

        // Initialize GameStateBus (hook SceneTree::idle) after mods are loaded.
        // The idle hook fires when the engine enters its first frame, so the
        // SceneTree is guaranteed to be available by then.
        // If the engine hasn't started yet, the hook will lazily init on first call.
        try
        {
            if (GameStateBus.Initialize())
            {
                Logger.Information("GameStateBus: idle hook active, {Count} readers registered",
                    GameStateBus.ReaderCount);

                // Relay GameStateSnapshot to ModEvents for monitoring
                EventBus.Subscribe<GameStateSnapshot>(snap =>
                {
                    if (snap.TickCount % 300 == 0) // ~every 5 seconds
                        EventBus.Publish(new ModEvents.GameStatePublish(
                            snap.TickCount, snap.Delta, snap.Extra.Count));
                });
            }
            else
            {
                Logger.Warning("GameStateBus: idle hook NOT active — will try lazy init on first idle call");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "GameStateBus initialization failed — game state API unavailable");
        }

        // Log cache stats when loader finishes
        EventBus.Subscribe<ModEvents.LoaderPhase>(_ =>
        {
            Logger.Information("Cache ready: {Hits} hits / {Misses} misses",
                Cache.HitCount, Cache.MissCount);
        });
    }
}
