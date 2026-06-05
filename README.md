# SlotWeave · Luck be a Landlord

Godot 3.4.4 runtime mod injection framework for [*Luck be a Landlord*](https://store.steampowered.com/app/1404850/Luck_be_a_Landlord/).

<div align="center">

**English** · [简体中文](https://github.com/Piraeus42/SlotWeave/blob/main/README_zh.md)

</div>

---

## What It Does

SlotWeave gives you **two complementary APIs** to mod the game — no Unity, no Mono, no IL2CPP. Just Godot's native runtime:

| API | How | Best for |
|-----|-----|----------|
| **GameStateBus** | Pure C# — read engine memory directly | Dashboards, overlays, data collection |
| **Patch** | GDScript source injection with `[Prefix]` / `[Postfix]` / `[Replace]` | Changing game behavior, redirecting functions |

---

## Why SlotWeave

- **Automatic syntax validation** — every patched script is tokenizer-checked. Syntax errors get a line number, not an access violation.
- **Reload loop breaker** — malformed output causing infinite recompile? Auto-blacklisted in 2 seconds.
- **Sentinel auto-guard** — framework prevents double-injection. No manual "did I already run?" checks in your mod code.
- **EmbeddedGd** — write GDScript in `.gd` files with full IDE support, not as escaped C# strings.
- **EventBus + Cache** — subscribe to load-time events. Patched sources are SHA256-cached so mods don't recompile every time.
- **Provenance tracking** — dump patched scripts with per-line `[ModName]` annotations for debugging.

> **Architecture**: [`docs/architecture.md`](docs/architecture.md) · **IDA data**: [`docs/ida_analysis_results.md`](docs/ida_analysis_results.md) · **Mod guide**: [`docs/mod-development-guide.md`](docs/mod-development-guide.md)

---

## Installation

1. Copy `winmm.dll` next to `Luck be a Landlord.exe`
2. Copy the `SlotWeave/` folder to the same directory
3. Drop mods into `SlotWeave/mods/<ModId>/` (one folder per mod, with `manifest.json` + `.dll`)
4. Launch — **.NET 8 Desktop Runtime** required

```
Luck be a Landlord/
├── Luck be a Landlord.exe
├── winmm.dll                ← proxy loader
├── SlotWeave/
│   ├── core/                ← framework runtime
│   ├── mods/                ← your mods
│   ├── configs/             ← auto-generated
│   └── SlotWeave.log        ← debug log
```

### Environment Variables

| Variable | Effect |
|----------|--------|
| `GDWEAVE_CONSOLE=1` | Allocate console window |
| `GDWEAVE_DEBUG=1` | Verbose logging |
| `GDWEAVE_DUMP_SOURCE=1` | Dump original scripts to `scripts/` |
| `GDWEAVE_DUMP_PATCHED=1` | Dump patched scripts with provenance to `scripts_patched/` |
| `GDWEAVE_STRICT_SANDBOX=1` | Refuse to write scripts with syntax errors |
| `GDWEAVE_NO_CACHE=1` | Disable patch cache |

### Building from Source

```bash
dotnet build -c Debug -p:SlotWeavePath="D:\steam\steamapps\common\Luck be a Landlord"
cargo build --release   # Rust loader
```

---

## Mod Development

Every mod is a .NET 8 assembly with a `manifest.json`:

```json
{
  "Id": "MyMod",
  "AssemblyPath": "MyMod.dll",
  "Dependencies": [],
  "Metadata": { "Name": "My Mod", "Version": "1.0.0", "Author": "Me" }
}
```

**Field names are PascalCase.** Lifecycle: `OnLoad()` → `OnInitialize()` → patches apply → game runs → `OnUnload()` → `Dispose()`.

> **Full guide**: [`docs/mod-development-guide.md`](docs/mod-development-guide.md) — covers Sentinel, Priority, ReplaceHelper, and `[Patch]`/`ISourceMod` interaction rules.

---

## API 1: GameStateBus

**Read game state from C#. Zero GDScript required.**

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

Register a custom reader:

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

mi.RegisterGameStateReader(new MyReader());
```

| Method | Description |
|--------|-------------|
| `FindNode("Main/Coins")` | Find node by Godot path |
| `GetChildNames(node)` | Enumerate child nodes |
| `ReadScriptProp(node, "propName")` | Read a GDScript variable via `GDScriptInstance::get` |

---

## API 2: Patch

**Inject GDScript with attribute-based declarative patches.**

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
| `[Replace]` | Replace function body | `static string Code(string original)` |

### EmbeddedGd

Write GDScript in `.gd` files — no escaped strings:

```
MyMod/Patches/gd/coins_replace.gd   ← pure GDScript, full IDE highlighting
```

```csharp
[Replace]
static string ReplaceCode(string original)
    => EmbeddedGd.Read(typeof(CoinsPatch), "gd.coins_replace.gd");
```

### ISourceMod

Low-level source transform API with auto-guard and priority:

```csharp
public class MySourceMod : ISourceMod
{
    public string? Sentinel => "func _my_helper"; // auto double-injection guard
    public int Priority => 10;                    // run before lower-priority mods
    public bool ShouldRun(string path) => path == "res://Main.tscn::1";

    public string Modify(string path, string source)
    {
        source = ReplaceHelper.ReplaceCall(source, "rand_range", "_my_rng_range");
        return ReplaceHelper.AppendCode(source, MyHelpers);
    }
}
```

---

## IModInterface

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

### Logging

```csharp
var log = mi.Logger.ForContext("SourceContext", "MyMod");
log.Information("Mod loaded");
log.Debug("Path={Path}", mi.GameDir);   // only when GDWEAVE_DEBUG=1
log.Warning("Something unusual");
log.Error(exception, "Fatal error");
```

---

## Troubleshooting

| Symptom | Check |
|---------|-------|
| `HostingError(InvalidConfigFile)` | .NET 8 Desktop Runtime missing |
| Game crashes on launch | Last lines of `SlotWeave.log` |
| Mod not working | Manifest PascalCase? Path/function matching? |
| No console output | `GDWEAVE_CONSOLE=1`? |
| Patched script syntax error | `GDWEAVE_DUMP_PATCHED=1`; use `EmbeddedGd` |
| Crash after game update | RVAs may have changed |

---

## Compatibility & Legal

**Version pinned** to Godot 3.4.4 build `419e713a2`. Game updates may break RVA offsets.

> ## ⚖️ Content Policy
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

---

## Credits

SlotWeave is an independent fork of [GDWeave](https://github.com/NotNite/GDWeave) by [NotNite](https://github.com/NotNite). Licensed under the same terms.
