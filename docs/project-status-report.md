# SlotWeave GameStateBus — 项目状态报告 (Final)

> **日期**: 2026-05-31
> **游戏**: Luck be a Landlord (Godot 3.4.4, `419e713a2`)
> **基址**: `0x140000000` (MSVC x64, stripped, ASLR)

---

## RVA 最终状态

| # | 目标 | RVA/偏移 | 状态 |
|---|------|---------|------|
| 1 | SceneTree::idle() | `0x787930` | ✅ |
| 2 | OS singleton | `0x2048AB8` | ✅ |
| 3 | get_main_loop() vtable | slot `0x330` | ✅ |
| 4 | SceneTree.root | `0x138` | ✅ |
| 5 | Node.children CowData | `0x108` | ✅ |
| 6 | Node.name StringName | `0x120` | ✅ |
| 7 | CowData::resize | `0x14D10` | ✅ |
| 8 | Variant::clear() | `0x1513D20` | ✅ |
| 9 | StringName ctor/dtor | `0x14AA130`/`0x14A9DB0` | ✅ |
| 10 | Node.script_instance | `0x58` | ✅ |
| 11 | GDScriptInstance vtable[0] = set | vtable+0x00 | ✅ |
| **12** | **GDScriptInstance::get** | **`0x1A1D30`** | ✅ vtable[1], 运行时提取 |

---

## 工作调用链

```
Node + 0x58 → GDScriptInstance*
GDScriptInstance::get(instance, StringName("coins"), &Variant(24b))
    RCX = instance, RDX = &prop, R8 = &ret, AL = bool
Variant → Marshal to C# → GameStateSnapshot.Extra["coins"]
```

---

## 文件结构

### 新增 (core)
- `NativeInterop/Native.cs` — RVA, Variant struct, delegates
- `NativeInterop/EngineObjectReader.cs` — FindNode, ReadScriptProp, NativeStringName/NativeVariant
- `GameState/GameStateBus.cs` — idle hook, reader registry, snapshot publish
- `GameState/GameStateSnapshot.cs` — data model
- `GameState/IGameStateReader.cs` — reader interface

### 新增 (mod)
- `DiagnosticMod/DiagnosticMod.cs` — console display
- `DiagnosticMod/GameDataReader.cs` — named property reads
- `DiagnosticMod/manifest.json`

### 修改
- `IModInterface.cs` / `ModInterface.cs` / `ModLoader.cs` / `SlotWeave.cs` — GameStateBus integration
- `ConsoleFixer.cs` — disable Quick Edit
- `ModEvents.cs` — GameStatePublish
- `run-lbl-debug.bat` — ASCII launcher

---

## 已验证的游戏数据

`EngineObjectReader.ReadScriptProp(node, property)` 成功读取:
- Main/Coins: `coins` (REAL), `queued_increase` (REAL)
- Main/Reels: `spinning` (BOOL), `effects_playing` (BOOL)
- 待验证: Main/Pop-up 路径

## 已知限制

- RVA 绑定到当前二进制，游戏更新需重验
- Pop-up 节点 FindNode 未命中 ("Main/Pop-up" vs 实际路径)
- WinForms 不可用 (无 WindowsDesktop 运行时)
- 仅标量类型 (无 Array/Dict 深度 Marshal)
