# SlotWeave Architecture

> Godot 3.4.4 build `419e713a2` · Luck be a Landlord

---

## Why Hook `GDScript::reload()`

LABL stores core game logic as inline scripts inside `.tscn` scene files. Godot's `ResourceFormatLoaderGDScript` only handles standalone `.gd`/`.gdc` files — it cannot intercept inline scripts.

`reload()` is the **single choke point** where every script passes before compilation:

- `this->source` (plaintext) is populated
- `this->path` (resource path) is set
- `GDScriptParser` (AST) has not started yet

This is the injection window.

---

## Project Structure

```
SlotWeave/
├── SlotWeave.cs              Entry point: Init() → assemble pipeline
├── Hooks.cs                  Core: GDScript::reload hook, memory read/write
├── IMod.cs                   Mod lifecycle: OnLoad / OnInitialize / OnUnload
├── IModInterface.cs          Mod API surface
├── Interop.cs                Signature scan + hook creation (Reloaded.Hooks)
├── MemoryUtils.cs             Memory read/write helpers
├── ConsoleFixer.cs            AllocConsole + disable Quick Edit
├── EventBus.cs                Internal pub/sub (thread-safe)
├── ModEvents.cs               Event record types
├── CacheManager.cs            SHA256-based source patch cache
├── Modding/
│   ├── ISourceMod.cs          Source-level mod interface
│   ├── SourceModder.cs        Sequential execution + provenance tracking
│   ├── GdTokenizer.cs         GDScript tokenizer (validation-only)
│   └── ReplaceHelper.cs       Safe regex-based function renaming
├── Scripting/
│   ├── ScriptInfo.cs          GDScript structure parser
│   ├── PatchAttribute.cs      [Patch]/[Prefix]/[Postfix]/[Replace] attributes
│   ├── PatchManager.cs        Patch registration + conflict-aware application
│   ├── PatchSourceMod.cs      PatchManager → ISourceMod adapter
│   └── EmbeddedGd.cs          Load .gd files as EmbeddedResource
├── GameState/
│   ├── GameStateBus.cs        SceneTree::idle hook + reader registry
│   ├── GameStateSnapshot.cs   Per-frame snapshot record
│   └── IGameStateReader.cs    Reader interface
├── NativeInterop/
│   ├── Native.cs              RVA constants, Variant struct, native delegates
│   └── EngineObjectReader.cs  FindNode, ReadScriptProp, NativeVariant
└── Loader/
    ├── ModLoader.cs           Discovery, dependency sort, load, lifecycle
    ├── ModInterface.cs        IModInterface implementation
    ├── ModManifest.cs         manifest.json model
    ├── ModLoadContext.cs      AssemblyLoadContext
    └── LoadedMod.cs           Mod data model
```

---

## Pipeline

```
Game loads a script
    ↓
GDScript::reload() intercepted (Hooks.ReloadDetour)
    ↓
[1] Pointer dedup check    → already patched this GDScript*? → skip
[2] Reload loop breaker    → ≥3 reloads in 2s? → blacklist
[3] Cache lookup           → SHA256(source + mod IDs + versions) hit? → use cached
[4] SourceModder pipeline:
    ├── PatchSourceMod     → [Patch] attributes (structured, all see original)
    └── ISourceMod #1..N   → sequential string transforms (each sees prior output)
[5] Tokenizer validation   → syntax error? → warn (or refuse if STRICT_SANDBOX)
[6] Write back             → Marshal.Copy (in-place) or CowData::resize (expansion)
[7] Cache store            → save result for next load
```

### Pipeline ordering

```
[Patch] (PatchSourceMod)     ← always FIRST, operates on original ScriptInfo
    ↓
ISourceMod mods              ← sorted by Priority desc, then dependency order
```

**[Patch] Replace methods all see the original source** — they cannot compose on each other's output. ISourceMod is sequential by design.

---

## Double-Injection Prevention

Three layers, each covering a different scenario:

| Layer | Mechanism | Guards against |
|-------|-----------|---------------|
| `patchedScripts` | `HashSet<IntPtr>` — GDScript pointer dedup | Same object reloaded via different paths |
| `Sentinel` | `ISourceMod.Sentinel` property — framework checks `source.Contains(sentinel)` before each mod | Mod code already present from prior run |
| Reload loop breaker | ≥3 reloads / 2s → blacklist | Infinite recompile loops from malformed output |

The pointer dedup (`patchedScripts`) catches Godot's multi-path alias issue. The Sentinel guard catches the case where Godot creates a fresh GDScript object for the same source. Together they cover both known double-injection paths.

---

## Source Write Strategy

| Path | Trigger | Mechanism |
|------|---------|-----------|
| In-place overwrite | New content ≤ old buffer capacity | `Marshal.Copy` + update size field |
| Engine resize | New content > old buffer capacity | Call Godot's `CowData::resize()`, then `Marshal.Copy` + null terminator |

All memory operations stay within the engine's allocator — no cross-allocator boundary issues.

---

## Verified RVAs & Offsets

### Function RVAs

| # | Target | RVA | Signature |
|---|--------|-----|-----------|
| 1 | `GDScript::reload()` | signature-scanned | See `patterns.txt` |
| 2 | `SceneTree::idle()` | `0x787930` | `bool(IntPtr, float)` |
| 3 | `CowData::resize` | `0x14D10` | `int(IntPtr, int)` |
| 4 | `Variant::clear()` | `0x1513D20` | `void(IntPtr)` |
| 5 | `StringName::ctor` | `0x14AA130` | `void(IntPtr, IntPtr)` |
| 6 | `StringName::dtor` | `0x14A9DB0` | `void(IntPtr)` |
| 7 | `GDScriptInstance::get` | `0x1A1D30` | `bool(IntPtr, IntPtr, IntPtr)` |
| 8 | `OS singleton (global)` | `0x2048AB8` | Direct global read |

### Memory Offsets

| Struct | Offset | Type | Purpose |
|--------|--------|------|---------|
| GDScript | `0x248` | String | source |
| GDScript | `0x250` | String | path |
| Resource | `0x108` | String | path (fallback) |
| SceneTree | `0x138` | Viewport* | root |
| Node | `0x58` | GDScriptInstance* | script_instance |
| Node | `0x108` | CowData<Node*> | children |
| Node | `0x120` | StringName | name |
| Node | `0xF0` | Node* | parent |
| OS vtable | `0x330` | slot | get_main_loop() |
| Variant | 24 bytes | struct | type(4) + pad(4) + data(16) |

All pinned to Godot 3.4.4 `419e713a2`. Game updates require re-verification.

---

## EventBus

Internal pub/sub system for loader events. Mods subscribe via `IModInterface.Subscribe<T>()`. Pure C#, no FFI.

| Event | When |
|-------|------|
| `ModLoaded` | After mod assembly loads + `OnLoad()` completes |
| `ScriptPatched` | After each script completes the SourceModder pipeline |
| `CacheHit` | Cached result reused, pipeline skipped |
| `CacheStored` | Patch result written to cache |
| `LoaderPhase` | Loader lifecycle (`"Starting"` / `"Ready"`) |
| `GameStatePublish` | Every ~300th frame (GameStateBus snapshot stats) |

Handlers wrapped in try-catch — one subscriber's error never takes down others.

## Cache

```
Key = SHA256(original_source + "\0" + sorted_mod_ids + "\0" + sorted_versions)

Memory layer (Dictionary) → disk layer (JSON files, SlotWeave/cache/)

Invalidated on: source change, mod list change, mod version bump, SlotWeave version change
```

---

## Debug Environment Variables

| Variable | Effect |
|----------|--------|
| `GDWEAVE_CONSOLE=1` | Allocate console window |
| `GDWEAVE_DEBUG=1` | Verbose logging |
| `GDWEAVE_DUMP_SOURCE=1` | Write original scripts to `scripts/` |
| `GDWEAVE_DUMP_PATCHED=1` | Write patched scripts with provenance to `scripts_patched/` |
| `GDWEAVE_STRICT_SANDBOX=1` | Refuse to write scripts with syntax errors |
| `GDWEAVE_NO_CACHE=1` | Disable patch cache |
| `GDWEAVE_FOLDER_OVERRIDE=...` | Custom SlotWeave directory path |

---

## Further Reference

- **IDA analysis**: [`ida_analysis_results.md`](ida_analysis_results.md) — full reverse-engineering data
- **Engine internals**: [`tscn_builtin_script_loading_chain.md`](tscn_builtin_script_loading_chain.md) — Godot's TSCN script loading chain
- **Binary patterns**: [`patterns.txt`](patterns.txt) — signature scan patterns for `GDScript::reload()`
- **Mod development**: [`mod-development-guide.md`](mod-development-guide.md) — Sentinel, Priority, ReplaceHelper, best practices
