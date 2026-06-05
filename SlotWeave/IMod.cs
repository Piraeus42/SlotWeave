namespace SlotWeave;

public interface IMod : IDisposable
{
    /// <summary>Called immediately after the mod assembly is loaded.</summary>
    void OnLoad() { }

    /// <summary>Called after all mods have been loaded and before hooks activate.</summary>
    void OnInitialize() { }

    /// <summary>Called when the game is shutting down.</summary>
    void OnUnload() { }
}
