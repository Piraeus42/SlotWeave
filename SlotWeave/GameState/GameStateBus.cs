// Layer 2: GameStateBus — hooks SceneTree::idle() and publishes per-frame snapshots
// The bus is the central coordinator between the engine hook and mod consumers.
//
// Architecture:
//   SceneTree::idle() hook (entry point)
//     → call original idle(float delta) → all _process/_physics_process run
//     → original returns (game state is consistent)
//     → GameStateBus.FireFrame(delta)
//       → Build snapshot
//       → Invoke all registered IGameStateReaders
//       → EventBus.Publish(snapshot)

using System.Runtime.InteropServices;
using SlotWeave.NativeInterop;
using Serilog;

namespace SlotWeave.GameState;

/// <summary>
/// Central game state bus. Hooks SceneTree::idle() and publishes
/// GameStateSnapshot each frame via EventBus.
///
/// Lifecycle:
///   1. Initialize() — hook idle, set up EngineObjectReader
///   2. Mods register IGameStateReader via RegisterReader()
///   3. Each frame: idle fires → readers run → snapshot published
///   4. Shutdown() — unhook idle, clean up
/// </summary>
public class GameStateBus : IDisposable
{
    private static readonly ILogger Logger = SlotWeave.Logger.ForContext<GameStateBus>();

    private readonly EngineObjectReader _reader;
    private readonly List<IGameStateReader> _readers = [];
    private readonly object _readersLock = new();
    private readonly Interop _interop;

    private ITrackedHook<Native.SceneTreeIdleDelegate>? _idleHook;
    private bool _hooked;
    private bool _disposed;

    /// <summary>Number of frames processed since initialization.</summary>
    public long FrameCount { get; private set; }

    /// <summary>The most recent snapshot. Updated atomically each frame.</summary>
    public GameStateSnapshot LatestSnapshot { get; private set; } = GameStateSnapshot.Empty;

    /// <summary>Whether the idle hook is active.</summary>
    public bool IsHooked => _hooked;

    /// <summary>Number of registered game state readers.</summary>
    public int ReaderCount { get { lock (_readersLock) { return _readers.Count; } } }

    public GameStateBus(Interop interop)
    {
        _interop = interop;
        _reader = new EngineObjectReader();
    }

    /// <summary>
    /// Initialize the EngineObjectReader and hook SceneTree::idle().
    /// Must be called after the game engine is fully initialized.
    /// If initialization fails (engine not ready yet), the hook will retry
    /// lazily on the first idle call.
    /// </summary>
    public bool Initialize()
    {
        if (_hooked)
        {
            Logger.Warning("GameStateBus already initialized");
            return true;
        }

        // Try to init the reader now (may fail if engine not yet started)
        _reader.Initialize();

        // Hook SceneTree::idle() at entry point
        var idleAddr = Native.BaseAddress + Native.RVA_SCENETREE_IDLE;
        Logger.Information("Hooking SceneTree::idle at 0x{Addr:X16}", idleAddr.ToInt64());

        try
        {
            _idleHook = _interop.CreateHook<Native.SceneTreeIdleDelegate>(idleAddr, IdleDetour);
            _idleHook.Enable();
            _hooked = true;
            Logger.Information("GameStateBus initialized — idle hook active, reader ready={Ready}",
                _reader.IsInitialized);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to hook SceneTree::idle()");
            return false;
        }
    }

    /// <summary>Register a game state reader. Safe to call from any thread.</summary>
    public void RegisterReader(IGameStateReader reader)
    {
        lock (_readersLock)
        {
            _readers.Add(reader);
            Logger.Debug("Registered reader: {ReaderType}", reader.GetType().Name);
        }
    }

    /// <summary>Unregister a game state reader.</summary>
    public void UnregisterReader(IGameStateReader reader)
    {
        lock (_readersLock)
        {
            _readers.Remove(reader);
            Logger.Debug("Unregistered reader: {ReaderType}", reader.GetType().Name);
        }
    }

    /// <summary>
    /// The hook detour for SceneTree::idle(float delta).
    /// Calls original (which runs all _process callbacks), then fires the bus.
    /// On first call, lazily initializes the EngineObjectReader if it wasn't ready.
    /// </summary>
    private bool IdleDetour(IntPtr sceneTree, float delta)
    {
        // Run the real idle() — this executes all _process/_physics_process callbacks
        bool shouldQuit;
        try
        {
            shouldQuit = _idleHook!.Original(sceneTree, delta);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SceneTree::idle() original threw — bailing out");
            return true; // Signal quit on error
        }

        // Lazy-init the reader if it wasn't ready at startup
        if (!_reader.IsInitialized)
        {
            try
            {
                if (_reader.Initialize(quiet: true))
                {
                    Logger.Information("GameStateBus lazy-init succeeded on frame {N}", FrameCount + 1);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GameStateBus lazy-init failed — will retry next frame");
            }
        }

        // Game state is now consistent — fire the bus
        if (_reader.IsInitialized)
        {
            try
            {
                FireFrame(delta);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GameStateBus FireFrame threw — skipping this frame");
            }
        }

        return shouldQuit;
    }

    /// <summary>Build and publish a snapshot for the current frame.</summary>
    private void FireFrame(float delta)
    {
        FrameCount++;

        var snapshot = new GameStateSnapshot
        {
            TickCount = Environment.TickCount64,
            Delta = delta
        };

        // Run all registered readers
        IGameStateReader[] readersSnapshot;
        lock (_readersLock)
        {
            readersSnapshot = _readers.ToArray();
        }

        foreach (var reader in readersSnapshot)
        {
            try
            {
                reader.Read(_reader, _reader.SceneTreePtr, snapshot);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GameStateReader {ReaderType} threw — skipping",
                    reader.GetType().Name);
            }
        }

        // Atomic update of latest snapshot
        LatestSnapshot = snapshot;

        // Publish to EventBus for mod consumers
        EventBus.Publish(snapshot);

        // Throttled logging
        if (FrameCount % 300 == 0) // ~every 5 seconds at 60fps
        {
            Logger.Debug("Frame {N}: {ReaderCount} readers, snapshot keys: {Keys}",
                FrameCount, readersSnapshot.Length,
                string.Join(", ", snapshot.Extra.Keys));
        }
    }

    /// <summary>
    /// Get the EngineObjectReader for direct use by mods during initialization.
    /// Mods can use this to find nodes and set up their reader logic.
    /// </summary>
    public EngineObjectReader Reader => _reader;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _idleHook?.Disable();
        _idleHook?.Dispose();
        _idleHook = null;
        _hooked = false;

        lock (_readersLock)
        {
            _readers.Clear();
        }

        Logger.Information("GameStateBus shut down ({FrameCount} frames processed)", FrameCount);
    }
}
