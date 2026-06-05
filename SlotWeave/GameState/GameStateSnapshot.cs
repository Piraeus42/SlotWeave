// Layer 3: Strongly-typed game state data models for mod consumption
// All records are read-only snapshots captured at the SceneTree::idle() return point

using SlotWeave.NativeInterop;

namespace SlotWeave.GameState;

/// <summary>
/// Per-frame snapshot of game state, captured at SceneTree::idle() return.
/// Contains all data needed by UI overlays and external tools.
/// Published via EventBus each frame.
///
/// Mod developers subscribe with:
///   modInterface.Subscribe&lt;GameStateSnapshot&gt;(snap => { ... });
/// </summary>
public record GameStateSnapshot
{
    /// <summary>Frame timestamp (Environment.TickCount64).</summary>
    public long TickCount { get; init; }

    /// <summary>SceneTree idle delta time for this frame.</summary>
    public float Delta { get; init; }

    /// <summary>Current coin count (if available).</summary>
    public long Coins { get; init; }

    /// <summary>Current turn number (if available).</summary>
    public long Turn { get; init; }

    /// <summary>Items recently destroyed this frame.</summary>
    public IReadOnlyList<string> RecentlyDestroyedItems { get; init; } = [];

    /// <summary>Current spin result symbols (if mid-spin).</summary>
    public IReadOnlyList<string> ReelSymbols { get; init; } = [];

    /// <summary>Whether the spin is currently active.</summary>
    public bool IsSpinning { get; init; }

    /// <summary>Whether the game is in a pop-up / modal state.</summary>
    public bool IsPopupVisible { get; init; }

    /// <summary>
    /// Extension data bag — mods can attach additional typed data here.
    /// Use string keys to avoid type collisions between mods.
    /// </summary>
    public Dictionary<string, object?> Extra { get; init; } = new();

    /// <summary>Empty snapshot (used as default before first frame).</summary>
    public static GameStateSnapshot Empty { get; } = new();
}

/// <summary>
/// Describes a named property to read from a node.
/// Used by IGameStateReader to declare its data dependencies.
/// </summary>
public record PropertyBinding
{
    /// <summary>Godot node path (e.g. "Main/Items").</summary>
    public string NodePath { get; init; } = "";

    /// <summary>Property name to read via Object::get().</summary>
    public string Property { get; init; } = "";

    /// <summary>C# target key in the snapshot (optional, defaults to Property).</summary>
    public string? TargetKey { get; init; }

    /// <summary>Expected variant type for type-safe reading.</summary>
    public VariantType ExpectedType { get; init; } = VariantType.Nil;
}

/// <summary>
/// Result of reading a single property binding.
/// </summary>
public record PropertyValue
{
    public string Key { get; init; } = "";
    public VariantType Type { get; init; }
    public object? Value { get; init; }
    public bool Success { get; init; }
}
