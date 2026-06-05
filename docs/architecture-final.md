# SlotWeave 架构文档 (Final)

> 2026-06-01 | Godot 3.4.4 | Luck be a Landlord

---

## 一、项目文件层次

```
SlotWeave/
├── SlotWeave.sln
├── run-lbl-debug.bat              # 启动器: 复制 DiagnosticMod + 设 env + 启动游戏
├── build-and-run.bat              # Release 构建脚本
├── .gitignore
├── README.md
├── CONTRIBUTING.md
│
├── docs/
│   ├── architecture.md            # 旧版架构 (GDScript reload 时代)
│   ├── architecture-final.md      # 本文档 — 最终架构
│   ├── ida_analysis_results.md    # IDA 反编译结果 (RVA/结构体/调用约定)
│   ├── project-status-report.md   # 项目状态报告 (开发里程碑)
│   └── events-cache.md            # 事件缓存机制说明
│
├── scripts/                       # dump 出的 GDScript 源码摘要
│   ├── Main.tscn__1.gd.summary.md
│   ├── Main.tscn__4.gd.summary.md
│   ├── Coins.tscn__1.gd.summary.md
│   ├── Items.tscn__1.gd.summary.md
│   ├── Reel.tscn__1.gd.summary.md
│   ├── Pop-up.tscn__1.gd.summary.md
│   └── ...
│
├── SlotWeave/                       # ========== 核心项目 ==========
│   ├── SlotWeave.csproj             # net8.0, AllowUnsafeBlocks
│   │
│   ├── SlotWeave.cs                 # 入口: Init() → 组装管线
│   ├── ConsoleFixer.cs            # AllocConsole + 禁用 Quick Edit
│   ├── MemoryUtils.cs             # VirtualProtect / ReadRaw / WriteRaw
│   ├── Interop.cs                 # 签名扫描 + Hook 创建 (Reloaded.Hooks)
│   ├── Hooks.cs                   # GDScript::reload Hook + CowData/String 读写
│   ├── EventBus.cs                # 内部 pub/sub 事件总线
│   ├── ModEvents.cs               # 事件类型: ScriptPatched, ModLoaded, GameStatePublish
│   ├── CacheManager.cs            # SHA256 源码 patch 缓存
│   │
│   ├── IMod.cs                    # Mod 生命周期接口: OnLoad/OnInitialize/OnUnload
│   ├── IModInterface.cs           # Mod API: Logger, Subscribe, RegisterGameStateReader, ...
│   │
│   ├── NativeInterop/             # ── Layer 1: 底层 C++ 互操作 ──
│   │   ├── Native.cs              # RVA 常量, Variant 24-byte struct, 原生委托定义
│   │   │                          #   GDScriptInstanceGetDelegate (0x1A1D30)
│   │   │                          #   VariantClearDelegate (0x1513D20)
│   │   │                          #   StringNameCtor/Dtor (0x14AA130/0x14A9DB0)
│   │   └── EngineObjectReader.cs  # NativeStringName / NativeVariant (IDisposable)
│   │                              #   FindNode(path) — CowData 遍历
│   │                              #   GetChildNames(node) — 子节点发现
│   │                              #   ReadScriptProp(node, prop) — GDScriptInstance::get
│   │                              #   ReadBool/Int/Real/String 便捷方法
│   │
│   ├── GameState/                 # ── Layer 2+3: 状态总线 ──
│   │   ├── GameStateBus.cs        # Hook SceneTree::idle() @ 0x787930
│   │   │                          #   → 每帧 FireFrame() → 遍历 readers → Publish
│   │   │                          #   懒初始化 (first idle retry)
│   │   ├── GameStateSnapshot.cs   # 强类型 record: TickCount, Delta, Coins, Extra dict
│   │   └── IGameStateReader.cs    # IGameStateReader 接口 + DeclarativeStateReader 基类
│   │
│   ├── Modding/                   # ── 旧版 Patch API (保留兼容) ──
│   │   ├── ISourceMod.cs          # ShouldRun + Modify 接口
│   │   └── SourceModder.cs        # 链式执行器 + provenance 跟踪
│   │
│   ├── Scripting/                 # ── 旧版 [Patch] 属性 API (保留兼容) ──
│   │   ├── PatchAttribute.cs      # [Patch]/[Prefix]/[Postfix]/[Replace]
│   │   ├── PatchManager.cs        # Patch 注册 + GDScript 解析 + 应用
│   │   ├── ScriptInfo.cs          # GDScript 结构解析器
│   │   └── PatchSourceMod.cs      # PatchManager → ISourceMod 适配器
│   │
│   └── Loader/                    # ── Mod 加载器 ──
│       ├── ModLoader.cs           # 发现/排序/加载/生命周期
│       ├── ModInterface.cs        # IModInterface 实现 (委托 GameStateBus)
│       ├── ModManifest.cs         # manifest.json 模型
│       ├── ModLoadContext.cs      # AssemblyLoadContext
│       └── LoadedMod.cs           # Mod 数据模型
│
├── SlotWeave-LABL-Template/         # ========== Mod 模板 + 诊断 Mod ==========
│   ├── README.md
│   │
│   └── DiagnosticMod/             # 诊断 Mod — 实时游戏状态显示
│       ├── DiagnosticMod.csproj   # net8.0, 引用 SlotWeave
│       ├── manifest.json          # Id: gdweave.diagnostic
│       ├── DiagnosticMod.cs       # 入口: 注册 reader + Console 显示
│       ├── GameDataReader.cs      # IGameStateReader: 读 coins/spins/spinning 等
│       └── DebugOverlay.cs        # WinForms overlay (不可用 — 保留参考)
│
└── mods/                          # ========== 旧版 TestMod (保留) ==========
    └── TestMod/                   # [Patch] API 测试 Mod
```

---

## 二、核心调用链

```
┌──────────────────────────────────────────────────────────────────┐
│  Mod 消费层                                                        │
│  modInterface.Subscribe<GameStateSnapshot>(snap => { ... })      │
│  modInterface.RegisterGameStateReader(myReader)                   │
└────────────────────────────┬─────────────────────────────────────┘
                             │ EventBus.Publish (每帧)
┌────────────────────────────▼─────────────────────────────────────┐
│  GameStateBus (GameState/GameStateBus.cs)                         │
│  Hook SceneTree::idle() @ 0x787930                                │
│    → 调用 original idle (所有 _process 完成)                        │
│    → 遍历 IGameStateReader 列表                                    │
│    → 构建 GameStateSnapshot                                       │
│    → EventBus.Publish(snapshot)                                   │
└────────────────────────────┬─────────────────────────────────────┘
                             │ reader.Read(reader, sceneTree, snap)
┌────────────────────────────▼─────────────────────────────────────┐
│  GameDataReader (DiagnosticMod/GameDataReader.cs)                 │
│  EngineObjectReader.ReadScriptProp(node, "coins")                 │
└────────────────────────────┬─────────────────────────────────────┘
                             │
┌────────────────────────────▼─────────────────────────────────────┐
│  EngineObjectReader (NativeInterop/EngineObjectReader.cs)         │
│  FindNode(path) — 遍历 Node.children CowData @ 0x108              │
│  ReadScriptProp(node, prop):                                      │
│    Node + 0x58 → GDScriptInstance*                                │
│    GDScriptInstance::get(inst, StringName, &Variant) → bool       │
│    Variant → Marshal to C# type                                   │
└────────────────────────────┬─────────────────────────────────────┘
                             │
┌────────────────────────────▼─────────────────────────────────────┐
│  Native (NativeInterop/Native.cs)                                 │
│  GDScriptInstanceGetDelegate @ RVA 0x1A1D30 (vtable[1])          │
│    RCX = instance, RDX = &prop, R8 = &ret, AL = bool             │
│  VariantClear @ 0x1513D20 — 释放 Variant                         │
│  StringNameCtor @ 0x14AA130 / Dtor @ 0x14A9DB0                   │
└──────────────────────────────────────────────────────────────────┘
```

---

## 三、已验证 RVA 总表

| # | 目标 | RVA | 委托签名 | 状态 |
|---|------|-----|---------|------|
| 1 | SceneTree::idle | `0x787930` | `bool(IntPtr, float)` | ✅ |
| 2 | OS singleton | `0x2048AB8` | 直接读全局变量 | ✅ |
| 3 | get_main_loop vtable | `0x330` | `IntPtr(IntPtr)` | ✅ |
| 4 | CowData::resize | `0x14D10` | `int(IntPtr, int)` | ✅ |
| 5 | Variant::clear | `0x1513D20` | `void(IntPtr)` | ✅ |
| 6 | StringName::StringName | `0x14AA130` | `void(IntPtr, IntPtr)` | ✅ |
| 7 | StringName::~StringName | `0x14A9DB0` | `void(IntPtr)` | ✅ |
| 8 | GDScriptInstance::get | `0x1A1D30` | `bool(IntPtr, IntPtr, IntPtr)` | ✅ |

### 内存偏移

| 结构体 | 偏移 | 类型 | 用途 |
|--------|------|------|------|
| SceneTree | `0x138` | Viewport* | root |
| Node | `0x58` | GDScriptInstance* | script_instance |
| Node | `0x108` | CowData<Node*> | children |
| Node | `0x120` | _Data* | name (StringName) |
| Node | `0xF0` | Node* | parent |
| GDScriptInstance | `0x00` | vtable* | 虚函数表 |
| GDScriptInstance | `0x08` | — | vtable[1] = get |
| Variant | 24 bytes | struct | type(4b) + pad(4b) + data(16b) |
| CowData<T> | ptr-8 | int32 | refcount |
| CowData<T> | ptr-4 | int32 | size |

---

## 四、Mod 开发 API

### 新 API (GameStateBus)

```csharp
// 订阅每帧快照
modInterface.Subscribe<GameStateSnapshot>(snap => {
    Console.WriteLine($"Coins: {snap.Extra["coins"]}");
});

// 注册自定义 reader
modInterface.RegisterGameStateReader(new MyGameReader());

// 实现 reader
public class MyGameReader : IGameStateReader
{
    public void Read(EngineObjectReader reader, IntPtr sceneTree, GameStateSnapshot snap)
    {
        var node = reader.FindNode("Main/Coins");
        snap.Extra["coins"] = EngineObjectReader.ReadScriptProp(node, "coins");
    }
}
```

### 旧 API (保留兼容)

```csharp
// [Patch] 属性 — 文本替换
[Patch("res://Main.tscn::1", "_process")]
static class MyPatch { [Prefix] static string Code() => "..."; }

// ISourceMod — 源码级修改
modInterface.RegisterSourceMod(myMod);
```

---

## 五、数据流时序

```
SlotWeave.Main()
  → ConsoleFixer.Init()           # 分配控制台
  → Interop = new Interop()       # 签名扫描引擎
  → GameStateBus = new(Interop)   # 创建总线
  → ModLoader = new(..., bus)     # 加载 Mod → Mod.OnLoad() 可注册 reader
  → Hooks = new(...)              # Hook GDScript::reload
  → GameStateBus.Initialize()     # Hook SceneTree::idle

游戏启动:
  → SceneTree::idle() 首次触发
    → GameStateBus.IdleDetour()
      → original idle() 运行
      → EngineObjectReader.Initialize()  ← 懒初始化
        → OS singleton → get_main_loop → SceneTree → root ✅
      → FireFrame(delta)
        → IGameStateReader.Read() × N
        → GameStateSnapshot 填充
        → EventBus.Publish(snapshot)
        → Mod 的 Subscribe 回调触发

每一帧:
  → idle hook → FireFrame → readers → publish → mod callback
```
