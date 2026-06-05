using SlotWeave;
using SlotWeave.GameState;

namespace DiagnosticMod;

public class DiagnosticMod : IMod
{
    private IModInterface _mi = null!;
    private GameDataReader? _gameReader;
    private int _frameCount;

    public DiagnosticMod(IModInterface mi)
    {
        _mi = mi;
        mi.Logger.Information("[Diag] Loaded OK");

        _gameReader = new GameDataReader();
        mi.RegisterGameStateReader(_gameReader);
        mi.Logger.Information("[Diag] GameDataReader registered");

        mi.Subscribe<GameStateSnapshot>(OnSnapshot);
        mi.Logger.Information("[Diag] Console display active — watch this window");
    }

    private int _lastLineCount;

    private void OnSnapshot(GameStateSnapshot snap)
    {
        _frameCount++;
        if (_frameCount % 60 != 0) return;

        var d = snap.Extra;

        if (_lastLineCount > 0)
        {
            for (var i = 0; i < _lastLineCount; i++)
            {
                Console.SetCursorPosition(0, i);
                Console.Write(new string(' ', Console.WindowWidth - 1));
            }
        }
        Console.SetCursorPosition(0, 0);

        Console.WriteLine($"=== SlotWeave — LABL Live | Frame {snap.TickCount} dT={snap.Delta*1000:F0}ms ===");
        Console.WriteLine();

        PrintVal(d, "coins", "Coins");
        PrintVal(d, "queued_increase", "  Queued increase");
        PrintVal(d, "spinning", "  Spinning");
        PrintVal(d, "effects_playing", "  Effects playing");
        PrintVal(d, "spins", "Spins");
        PrintVal(d, "current_floor", "Floor");
        PrintVal(d, "max_floor", "  / max floor");
        PrintVal(d, "times_rent_paid", "  Rent paid");
        PrintVal(d, "reroll_tokens", "Tokens: Reroll");
        PrintVal(d, "removal_tokens", "  Removal");
        PrintVal(d, "essence_tokens", "  Essence");
        PrintVal(d, "endless_mode", "Endless mode");
        PrintVal(d, "total_peppers", "Peppers");

        Console.WriteLine();
        var nodes = _gameReader!.DiscoveredNodes;
        if (nodes.Length > 0)
            Console.WriteLine($"Nodes: {string.Join(", ", nodes)}");

        _lastLineCount = 20;
    }

    private static void PrintVal(Dictionary<string, object?> d, string key, string label)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return;
        var s = v switch
        {
            bool b => b ? "YES" : "no",
            double r => r.ToString("F1"),
            long i => i.ToString(),
            string str => str,
            _ => v.ToString() ?? "?"
        };
        Console.WriteLine($"{label}: {s}");
    }

    public void OnLoad() { }
    public void OnInitialize() { }
    public void OnUnload() { }
    public void Dispose() { }
}
