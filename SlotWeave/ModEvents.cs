namespace SlotWeave;

/// <summary>Event records published on the loader EventBus.</summary>
public static class ModEvents
{
    /// <summary>Fired after a script passes through the SourceModder pipeline.</summary>
    public record ScriptPatched(string Path, int OriginalLength, int PatchedLength, bool Modified);

    /// <summary>Fired after a mod assembly is loaded and OnLoad() completes.</summary>
    public record ModLoaded(string ModId, string Version);

    /// <summary>Fired when a cached patch result is reused instead of re-running SourceModder.</summary>
    public record CacheHit(string ScriptPath);

    /// <summary>Fired when a patched script result is stored in the cache.</summary>
    public record CacheStored(string ScriptPath);

    /// <summary>Fired when the loader starts and finishes initialization.</summary>
    public record LoaderPhase(string Phase); // "Starting", "Ready"

    /// <summary>Fired at each frame when the GameStateBus publishes a snapshot.</summary>
    public record GameStatePublish(long TickCount, float Delta, int ExtraKeyCount);
}
