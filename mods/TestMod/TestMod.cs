using SlotWeave;
using SlotWeave.Scripting;

public class Mod : IMod
{
    public Mod(IModInterface mi)
    {
        mi.Logger.Information("[TestMod] Loaded OK - build 1.0.7 (diag)");

        mi.Subscribe<ModEvents.ScriptPatched>(e =>
        {
            if (e.Modified)
                mi.Logger.Information("[TestMod] PATCHED {Path} {Orig} -> {New} chars",
                    e.Path, e.OriginalLength, e.PatchedLength);
        });
    }

    public void Dispose() { }
}

// ── Original test patches ──

[Patch("res://Options.tscn::2", "_ready")]
static class Test1_Prefix
{
    [Prefix]
    static string Code() => "print(\"[TestMod] Options._ready START\")";
}

[Patch("res://Options.tscn::2", "_ready")]
static class Test2_Postfix
{
    [Postfix]
    static string Code() => "print(\"[TestMod] Options._ready END\")";
}

[Patch("res://Coins.tscn::1", "_ready")]
static class Test3_Replace
{
    [Replace]
    static string Code(string original)
    {
        return "print(\"[TestMod] Coins._ready intercepted\")\n" + original;
    }
}

// ════════════════════════════════════════════════════════════════
// Diagnostic patches — simplified single-line to avoid parse errors
// ════════════════════════════════════════════════════════════════

// 1. Main._ready() start + end
[Patch("res://Main.tscn::1", "_ready")]
static class Diag_Ready
{
    [Prefix]
    static string Code() => "print(\"[TestMod] _ready START \", OS.get_ticks_msec())";

    [Postfix]
    static string CodePost() => "print(\"[TestMod] _ready END \", OS.get_ticks_msec())";
}

// 2. HTTP response callback
[Patch("res://Main.tscn::1", "_http_request_completed")]
static class Diag_HttpCallback
{
    [Prefix]
    static string Code() => "print(\"[TestMod] HTTP FIRED \", OS.get_ticks_msec(), \" code=\", response_code)";
}

// 3. Main.reload() trigger
[Patch("res://Main.tscn::1", "reload")]
static class Diag_MainReload
{
    [Prefix]
    static string Code() => "print(\"[TestMod] Main.reload() CALLED \", OS.get_ticks_msec())";
}

// 4. Main.title()
[Patch("res://Main.tscn::1", "title")]
static class Diag_Title
{
    [Prefix]
    static string Code() => "print(\"[TestMod] title() CALLED \", OS.get_ticks_msec())";
}

// 5. Menu path changes
[Patch("res://Main.tscn::1", "change_current_menu_path")]
static class Diag_MenuPath
{
    [Prefix]
    static string Code() => "print(\"[TestMod] menu -> \", path, \" \", OS.get_ticks_msec())";
}

// 6. _process — reload timer watch + heartbeat (combined, single-line ifs)
[Patch("res://Main.tscn::1", "_process")]
static class Diag_Process
{
    [Prefix]
    static string Code() => "if reload_scene_timer > 0: print(\"[TestMod] RELOAD_TIMER=\", reload_scene_timer, \" \", OS.get_ticks_msec())\nif frame_timer == 0: print(\"[TestMod] heartbeat \", OS.get_ticks_msec(), \" \", current_menu_path)";
}
