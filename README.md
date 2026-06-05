# SlotWeave · Luck be a Landlord

Godot 3.4.4 runtime mod injection framework for [*Luck be a Landlord*](https://store.steampowered.com/app/1404850/Luck_be_a_Landlord/). Provides two complementary APIs: **GameStateBus** (pure C# memory reads) and **Patch** (GDScript source injection).

<div align="center">

**English** · [简体中文](https://github.com/Piraeus42/SlotWeave/blob/main/README_zh.md)

</div>

---

> ## ⚖️ Licensing & Content Policy
>
> **SlotWeave and all official mods developed under this framework:**
>
> - ❌ Do **NOT** include, bundle, or redistribute **any** original game source code
> - ❌ Do **NOT** include, bundle, or redistribute **any** original game art assets, audio, or data files
> - ❌ Do **NOT** modify or patch the game binary on disk
> - ✅ Operate entirely through **runtime code injection** — mods are applied in memory at load time
> - ✅ Modder code is **100% original** — only the transformation logic written by the modder is distributed
> - ✅ Game assets and original scripts remain in the game installation and are never copied or repackaged
>
> **This is a deliberate, permanent design choice.** The framework injects behavior at runtime without ever shipping the game's intellectual property. Mods are transformers, not derivatives.

> **Architecture**: [`docs/architecture-final.md`](docs/architecture-final.md) · **IDA data**: [`docs/ida_analysis_results.md`](docs/ida_analysis_results.md)

---

## ⚠️ Version Pinning

All engine function RVAs are pinned to Godot 3.4.4 build `419e713a2`. **Game updates may break RVA offsets** and require re-verification.

---

## Quick Start

### Installation

1. Copy `winmm.dll` next to `Luck be a Landlord.exe`
2. Copy the `SlotWeave/` folder to the same directory
3. Drop mods into `SlotWeave/mods/<ModId>/` (one folder per mod, with `manifest.json` + `.dll`)
4. Launch — .NET 8 Desktop Runtime required

```
Luck be a Landlord/
├── Luck be a Landlord.exe
├── winmm.dll              ← SlotWeave loader (proxy DLL)
├── SlotWeave/
│   ├── core/              ← Framework runtime
│   ├── mods/              ← Mod assemblies
│   ├── configs/           ← Auto-generated config
│   └── SlotWeave.log      ← Debug log
```

### Debug Environment Variables

| Variable | Effect |
|----------|--------|
| `GDWEAVE_CONSOLE=1` | Allocate console window (essential for debugging) |
| `GDWEAVE_DEBUG=1` | Verbose logging |
| `GDWEAVE_DUMP_SOURCE=1` | Dump original scripts to `scripts/` |
| `GDWEAVE_DUMP_PATCHED=1` | Dump patched scripts with provenance annotations to `scripts_patched/` |
| `GDWEAVE_STRICT_SANDBOX=1` | Refuse to write scripts with syntax errors — fall back to original |
| `GDWEAVE_NO_CACHE=1` | Disable the patch cache |

### Building

```bash
# Build and deploy to game directory
dotnet build -c Debug -p:SlotWeavePath="D:\steam\steamapps\common\Luck be a Landlord"

# Rust loader
cargo build --release
```

---

## Mod Development

> **Full guide**: [`docs/mod-development-guide.md`](docs/mod-development-guide.md)

### manifest.json

```json
{
  "Id": "MyMod",
  "AssemblyPath": "MyMod.dll",
  "Dependencies": [],
  "Metadata": { "Name": "My Mod", "Version": "1.0.0", "Author": "Me" }
}
```

**Field names are PascalCase**: `Id` not `id`, `AssemblyPath` not `assemblyPath`.

### Lifecycle

```
Assembly loaded → OnLoad() → OnInitialize() → Script Patches → GameStateBus starts → Game runs → OnUnload() → Dispose()
```

---

## API 1: GameStateBus

**Read game state from C# without writing any GDScript.**

### Subscribe to Per-Frame Snapshots

```csharp
using SlotWeave;
using SlotWeave.GameState;

public class Mod : IMod
{
    public Mod(IModInterface mi)
    {
        mi.Subscribe<GameStateSnapshot>(snap =>
        {
            if (snap.Extra.TryGetValue("coins", out var c))
                mi.Logger.Information("Coins: {Coins}", c);
        });
    }
}
```

### Register a Data Reader

```csharp
using SlotWeave.GameState;
using SlotWeave.NativeInterop;

public class MyReader : IGameStateReader
{
    public void Read(EngineObjectReader reader, IntPtr sceneTree, GameStateSnapshot snap)
    {
        var node = reader.FindNode("Main/Coins");
        snap.Extra["coins"] = EngineObjectReader.ReadScriptProp(node, "coins");
    }
}

// In your mod constructor:
mi.RegisterGameStateReader(new MyReader());
```

### EngineObjectReader Methods

| Method | Description |
|--------|-------------|
| `FindNode("Main/Coins")` | Find node by Godot path |
| `GetChildNames(node)` | Enumerate child node names (discover tree structure) |
| `ReadScriptProp(node, "propName")` | Read a GDScript variable via `GDScriptInstance::get` |

### GameStateSnapshot

```csharp
public record GameStateSnapshot
{
    long TickCount;                              // Frame timestamp
    float Delta;                                 // Idle delta time
    Dictionary<string, object?> Extra;           // Extension data
}
```

---

## API 2: Patch (Syntax-Safe)

### Declarative `[Patch]`

```csharp
using SlotWeave.Scripting;

[Patch("res://Main.tscn::1", "get_coins")]
static class CoinsPatch
{
    [Prefix]
    static string PrefixCode() => "print('get_coins called')";

    [Replace]
    static string ReplaceCode(string original) => original.Replace("coins", "coins + 1");
}
```

| Attribute | Placement | Signature |
|-----------|-----------|-----------|
| `[Prefix]` | Before function body | `static string Code()` |
| `[Postfix]` | After function body | `static string Code()` |
| `[Replace]` | Replace entire function body | `static string Code(string original)` |

### EmbeddedGd: Write GDScript in `.gd` Files

Don't embed GDScript as escaped C# strings. Write `.gd` files as `EmbeddedResource`:

```
MyMod/
├── MyMod.csproj              # <EmbeddedResource Include="Patches/gd/*.gd" />
├── Mod.cs
└── Patches/
    ├── CoinsPatch.cs
    └── gd/
        └── coins_replace.gd   ← Pure GDScript, full IDE syntax highlighting
```

```csharp
[Patch("res://Main.tscn::1", "get_coins")]
static class CoinsPatch
{
    [Replace]
    static string ReplaceCode(string original)
        => EmbeddedGd.Read(typeof(CoinsPatch), "gd.coins_replace.gd");
}
```

### Automatic Syntax Validation (GdTokenizer)

Every patched script is tokenizer-validated after modification. Syntax errors include the **exact line number**:

```
[WARN] #42 [res://Slot Icon.tscn::1] Patched source has syntax error at line 128: Unclosed string literal
```

Set `GDWEAVE_STRICT_SANDBOX=1` to **refuse writing** invalid scripts — the game won't crash from malformed patches.

### Reload Loop Breaker

If a SourceMod produces invalid GDScript that causes Godot to endlessly reload the same script (≥3 times in 2 seconds), the path is automatically **blacklisted**:

```
[LOOP-BREAK] res://Tooltip.tscn::1 reloaded 3 times in 150ms — blacklisting
```

### ISourceMod Low-Level API

```csharp
using SlotWeave.Modding;

public class MySourceMod : ISourceMod
{
    public string? Sentinel => "func _my_helper"; // Framework auto-guard
    public int Priority => 10;                    // Run before lower-priority mods
    public bool ShouldRun(string path) => path == "res://Main.tscn::1";

    public string Modify(string path, string source)
    {
        // Use ReplaceHelper for safe function renames
        source = ReplaceHelper.ReplaceCall(source, "rand_range", "_my_rng_range");
        source = ReplaceHelper.AppendCode(source, MyHelpers);
        return source;
    }
}

mi.RegisterSourceMod(new MySourceMod());
```

---

## IModInterface Reference

```csharp
public interface IModInterface
{
    string GameDir { get; }
    string SlotWeaveDir { get; }
    ILogger Logger { get; }
    string[] LoadedMods { get; }

    T ReadConfig<T>() where T : class, new();
    void WriteConfig<T>(T config);

    void RegisterSourceMod(ISourceMod mod);
    void Subscribe<T>(Action<T> handler);

    void RegisterGameStateReader(IGameStateReader r);
    void UnregisterGameStateReader(IGameStateReader r);

    void ClearCache();
}
```

---

## Logging

```csharp
var log = mi.Logger.ForContext("SourceContext", "MyMod");
log.Information("Mod loaded");
log.Debug("Path={Path}", mi.GameDir);  // Only when GDWEAVE_DEBUG=1
log.Warning("Warning message");
log.Error(exception, "Fatal error");
```

---

## Troubleshooting

| Symptom | Check |
|---------|-------|
| `HostingError(InvalidConfigFile)` | .NET 8 Desktop Runtime not installed |
| Game crashes on launch | Last lines of `SlotWeave.log` |
| Mod not working | Are manifest field names PascalCase? Path/function matching? |
| No console output | Set `GDWEAVE_CONSOLE=1`? |
| Patched script syntax error | Set `GDWEAVE_DUMP_PATCHED=1` to inspect; use `EmbeddedGd` instead of string concatenation |
| Crash after game update | RVAs may have changed; wait for framework update |

---

## Credits

SlotWeave is an independent fork of [GDWeave](https://github.com/NotNite/GDWeave) by [NotNite](https://github.com/NotNite). Licensed under the same terms as the upstream project.
