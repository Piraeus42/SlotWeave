using SlotWeave.GameState;
using SlotWeave.Modding;
using Serilog;

namespace SlotWeave;

public interface IModInterface {
    // ── Logging ──
    public ILogger Logger { get; }

    // ── Paths ──
    public string GameDir { get; }
    public string SlotWeaveDir { get; }

    // ── Mod registry ──
    public string[] LoadedMods { get; }

    // ── Config ──
    public T ReadConfig<T>() where T : class, new();
    public void WriteConfig<T>(T config) where T : class;

    // ── Patching ──
    /// <summary>Register a source-level script mod.</summary>
    /// <remarks>Must be called before the game finishes initializing.</remarks>
    public void RegisterSourceMod(ISourceMod mod);

    // ── Events ──
    /// <summary>Subscribe to a loader event (ScriptPatched, ModLoaded, GameStateSnapshot, etc.).</summary>
    public void Subscribe<T>(Action<T> handler) where T : notnull;

    // ── Cache ──
    /// <summary>Clear all patched-script cache entries. Call after updating a mod.</summary>
    public void ClearCache();

    // ── GameState (pure C# memory-read API) ──
    /// <summary>
    /// Register a game state reader. Its Read() method is called every frame
    /// at the SceneTree::idle() return point, after all _process callbacks.
    /// Use this to read game state directly from engine memory without GDScript patching.
    /// </summary>
    public void RegisterGameStateReader(IGameStateReader reader);

    /// <summary>
    /// Unregister a previously registered game state reader.
    /// </summary>
    public void UnregisterGameStateReader(IGameStateReader reader);
}
