using SlotWeave.GameState;
using SlotWeave.NativeInterop;

namespace DiagnosticMod;

/// <summary>
/// Reads live game state via GDScriptInstance::get() — named property access.
/// RVA 0x1A1D30 extracted from vtable at runtime.
/// </summary>
public class GameDataReader : IGameStateReader
{
    private IntPtr _coinsNode;
    private IntPtr _itemsNode;
    private IntPtr _popupNode;
    private IntPtr _reelsNode;
    private bool _pathsResolved;
    private readonly string[] _discovered = new string[64];
    private int _discoveredCount;

    public string[] DiscoveredNodes
    {
        get { var c = new string[_discoveredCount]; Array.Copy(_discovered, c, _discoveredCount); return c; }
    }

    public void Read(EngineObjectReader reader, IntPtr sceneTree, GameStateSnapshot snapshot)
    {
        if (!_pathsResolved)
        {
            DiscoverNodes(reader);
            _pathsResolved = true;
        }

        // Coins
        if (_coinsNode != IntPtr.Zero)
        {
            snapshot.Extra["coins"] = EngineObjectReader.ReadScriptProp(_coinsNode, "coins");
            snapshot.Extra["queued_increase"] = EngineObjectReader.ReadScriptProp(_coinsNode, "queued_increase");
        }

        // Pop-up: spin count, floor, tokens
        if (_popupNode != IntPtr.Zero)
        {
            snapshot.Extra["spins"] = EngineObjectReader.ReadScriptProp(_popupNode, "spins");
            snapshot.Extra["current_floor"] = EngineObjectReader.ReadScriptProp(_popupNode, "current_floor");
            snapshot.Extra["max_floor"] = EngineObjectReader.ReadScriptProp(_popupNode, "max_floor");
            snapshot.Extra["times_rent_paid"] = EngineObjectReader.ReadScriptProp(_popupNode, "times_rent_paid");
            snapshot.Extra["reroll_tokens"] = EngineObjectReader.ReadScriptProp(_popupNode, "reroll_tokens");
            snapshot.Extra["removal_tokens"] = EngineObjectReader.ReadScriptProp(_popupNode, "removal_tokens");
            snapshot.Extra["essence_tokens"] = EngineObjectReader.ReadScriptProp(_popupNode, "essence_tokens");
            snapshot.Extra["endless_mode"] = EngineObjectReader.ReadScriptProp(_popupNode, "endless_mode");
        }

        // Reels: spinning state
        if (_reelsNode != IntPtr.Zero)
        {
            snapshot.Extra["spinning"] = EngineObjectReader.ReadScriptProp(_reelsNode, "spinning");
            snapshot.Extra["effects_playing"] = EngineObjectReader.ReadScriptProp(_reelsNode, "effects_playing");
        }

        // Items
        if (_itemsNode != IntPtr.Zero)
        {
            snapshot.Extra["total_peppers"] = EngineObjectReader.ReadScriptProp(_itemsNode, "total_peppers");
        }
    }

    private void DiscoverNodes(EngineObjectReader reader)
    {
        var main = reader.FindNode("Main");
        if (main != IntPtr.Zero)
        {
            foreach (var name in reader.GetChildNames(main))
                if (_discoveredCount < _discovered.Length)
                    _discovered[_discoveredCount++] = $"Main/{name}";

            _coinsNode = reader.FindNode("Main/Coins");
            _itemsNode = reader.FindNode("Main/Items");
            _popupNode = reader.FindNode("Main/Pop-up");
            _reelsNode = reader.FindNode("Main/Reels");
        }

        if (_coinsNode == IntPtr.Zero) _coinsNode = reader.FindNode("Coins");
        if (_itemsNode == IntPtr.Zero) _itemsNode = reader.FindNode("Items");
        if (_popupNode == IntPtr.Zero) _popupNode = reader.FindNode("Pop-up");
        if (_reelsNode == IntPtr.Zero) _reelsNode = reader.FindNode("Reels");
    }
}
