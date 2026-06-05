using SlotWeave;
using SlotWeave.GameState;
using Timer = System.Windows.Forms.Timer;

namespace DiagnosticMod;

/// <summary>
/// External WinForms debug overlay. Subscribes to GameStateSnapshot via EventBus,
/// stores latest data, refreshes UI on a timer. Runs on its own STA thread.
/// </summary>
public class DebugOverlay : Form
{
    private GameStateSnapshot _latest = GameStateSnapshot.Empty;
    private readonly GameDataReader _reader;
    private readonly Label _coinsLabel;
    private readonly Label _spinLabel;
    private readonly Label _floorLabel;
    private readonly Label _tokensLabel;
    private readonly Label _frameLabel;
    private readonly Label _nodesLabel;
    private readonly Timer _timer;

    public DebugOverlay(GameDataReader reader)
    {
        _reader = reader;

        Text = "SlotWeave — LABL Live State";
        Size = new Size(420, 320);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(50, 50);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);
        Font = new Font("Consolas", 11f);

        var y = 8;
        _ = MakeLabel("=== SlotWeave GameState Debug ===", ref y, Color.Cyan, 12f);
        _coinsLabel = MakeLabel("Coins: --", ref y, Color.Gold);
        _spinLabel = MakeLabel("Spin: --", ref y, Color.White);
        _floorLabel = MakeLabel("Floor: --", ref y, Color.White);
        _tokensLabel = MakeLabel("Tokens: --", ref y, Color.White);
        _frameLabel = MakeLabel("Frame: --", ref y, Color.Gray);
        _nodesLabel = MakeLabel("Nodes: discovering...", ref y, Color.Gray);
        _nodesLabel.MaximumSize = new Size(390, 120);
        _nodesLabel.AutoSize = true;

        // Use WinForms timer (ticks on UI thread) for safe label updates
        _timer = new Timer { Interval = 150 };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private Label MakeLabel(string text, ref int y, Color color, float? size = null)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(10, y),
            AutoSize = true,
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = size is float s ? new Font("Consolas", s) : Font
        };
        Controls.Add(lbl);
        y += 24;
        return lbl;
    }

    private static long GetLong(Dictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is long l ? l : 0;

    private static bool GetBool(Dictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v is true;

    private void OnTick(object? sender, EventArgs e)
    {
        var snap = _latest;
        if (snap.TickCount == 0) return;

        var d = snap.Extra;

        // Coins
        var coins = GetLong(d, "coins");
        var qi = GetLong(d, "queued_increase");
        if (d.ContainsKey("coins"))
            _coinsLabel.Text = qi != 0 ? $"Coins: {coins}  (+{qi} pending)" : $"Coins: {coins}";

        // Spin state
        var spinning = GetBool(d, "spinning");
        var effects = GetBool(d, "effects_playing");
        var counting = GetBool(d, "counting_symbols");
        if (spinning) { _spinLabel.Text = "Spin: RUNNING"; _spinLabel.ForeColor = Color.Lime; }
        else if (effects || counting) { _spinLabel.Text = effects ? "Spin: resolving effects..." : "Spin: counting..."; _spinLabel.ForeColor = Color.Orange; }
        else { _spinLabel.Text = "Spin: idle"; _spinLabel.ForeColor = Color.White; }

        // Floor & spins
        var floor = GetLong(d, "current_floor");
        var maxFloor = GetLong(d, "max_floor");
        var spins = GetLong(d, "spins");
        var rent = GetLong(d, "times_rent_paid");
        var endless = GetBool(d, "endless_mode");
        _floorLabel.Text = endless
            ? $"Floor: {floor}/{maxFloor} (ENDLESS)  Spins: {spins}  Rent: {rent}/12"
            : $"Floor: {floor}/{maxFloor}  Spins: {spins}  Rent: {rent}/12";

        // Tokens
        _tokensLabel.Text = $"Tokens: Reroll={GetLong(d, "reroll_tokens")}  " +
                            $"Removal={GetLong(d, "removal_tokens")}  Essence={GetLong(d, "essence_tokens")}";

        // Frame info
        _frameLabel.Text = $"Frame: {snap.TickCount}  dT={snap.Delta*1000:F0}ms  Peppers={GetLong(d, "total_peppers")}";

        // Discovered nodes
        var nodes = _reader.DiscoveredNodes;
        if (nodes.Length > 0 && _nodesLabel.Text == "Nodes: discovering...")
            _nodesLabel.Text = $"Nodes: {string.Join(", ", nodes)}";
    }

    /// <summary>Thread-safe snapshot update from EventBus callback (game thread).</summary>
    public void OnSnapshot(GameStateSnapshot snap) => _latest = snap;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Fire-and-forget launch on STA background thread.</summary>
    public static void Launch(GameDataReader reader)
    {
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var overlay = new DebugOverlay(reader);
            EventBus.Subscribe<GameStateSnapshot>(snap => overlay.OnSnapshot(snap));
            Application.Run(overlay);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }
}
