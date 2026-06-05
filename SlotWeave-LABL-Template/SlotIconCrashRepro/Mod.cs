using SlotWeave;
using SlotWeave.Modding;

namespace SlotIconCrashRepro;

/// <summary>
/// Minimal crash reproducer: appends ONE noop function to Slot Icon.tscn::1.
/// Zero regex, zero /root/Main references, zero RNG modification.
/// If this crashes, it proves Godot 3.4.4 has a bug where ANY source change
/// to a hot-path script (high-frequency instance creation/destruction) causes
/// memory corruption during live instance rebuild.
/// </summary>
public class Mod : IMod
{
    private IModInterface _mi = null!;

    public Mod(IModInterface mi)
    {
        _mi = mi;
        mi.Logger.Information("[CrashRepro] Loaded — this mod does ONE thing: append _bh_noop() to SlotIcon");
        mi.RegisterSourceMod(new SlotIconNoopSourceMod());
    }

    public void OnLoad() { }
    public void OnInitialize() { }
    public void OnUnload() { }
    public void Dispose() { }
}

/// <summary>
/// Appends a single empty function to Slot Icon.tscn::1.
/// </summary>
public class SlotIconNoopSourceMod : ISourceMod
{
    public bool ShouldRun(string path) =>
        path == "res://Slot Icon.tscn::1";

    public string Modify(string path, string source) =>
        source + "\nfunc _bh_noop():\n\tpass\n";
}
