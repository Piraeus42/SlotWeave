# SlotWeave · Luck be a Landlord

Godot 3.4.4 运行时 Mod 注入框架，针对《幸运房东》移植。提供两套互补 API：**GameStateBus**（纯 C# 内存直读）和 **Patch**（GDScript 源码注入）。

> 架构: [`docs/architecture-final.md`](docs/architecture-final.md) · IDA 数据: [`docs/ida_analysis_results.md`](docs/ida_analysis_results.md)

---

## ⚠️ 版本绑定

所有引擎函数 RVA 绑定到 Godot 3.4.4 build `419e713a2`。**游戏更新后 RVA 可能失效**，需重新验证。

---

## 快速开始

### 安装

1. `winmm.dll` → 游戏根目录（与 `Luck be a Landlord.exe` 同级）
2. `SlotWeave/core/` → 复制到游戏根目录
3. Mod → `SlotWeave/mods/<ModId>/`（每个 Mod 一个文件夹，内含 `manifest.json` + `.dll`）
4. 双击 `run-lbl.bat`（自动检测安装 .NET 8 运行时 + 启动游戏）

```
Luck be a Landlord/
├── Luck be a Landlord.exe
├── winmm.dll
├── SlotWeave/
│   ├── core/           # SlotWeave 运行时
│   ├── mods/           # Mod
│   ├── configs/        # 配置（自动生成）
│   └── SlotWeave.log     # 日志
```

### 环境变量

| 变量 | 用途 |
|------|------|
| `GDWEAVE_CONSOLE=1` | 分配控制台窗口（调试必备） |
| `GDWEAVE_DEBUG=1` | Verbose 日志 |
| `GDWEAVE_DUMP_SOURCE=1` | dump 原始脚本到 `scripts/` |
| `GDWEAVE_DUMP_PATCHED=1` | dump patched 脚本到 `scripts_patched/` |
| `GDWEAVE_STRICT_SANDBOX=1` | patched 源码有语法错误时回退原始源码 |
| `GDWEAVE_NO_CACHE=1` | 禁用 patch 缓存 |

### 构建

```bash
dotnet build -c Debug -p:SlotWeavePath="D:\steam\steamapps\common\Luck be a Landlord"
```

调试启动：`run-lbl-debug.bat`（构建 + 部署 DiagnosticMod + 控制台 + 启动）

---

## Mod 开发

### manifest.json

```json
{
  "Id": "MyMod",
  "AssemblyPath": "MyMod.dll",
  "Dependencies": [],
  "Metadata": { "Name": "My Mod", "Version": "1.0.0", "Author": "Me" }
}
```

**字段名 PascalCase**：`Id` 不是 `id`，`AssemblyPath` 不是 `assemblyPath`。

### 生命周期

```
DLL 载入 → OnLoad() → OnInitialize() → Script Patches → GameStateBus 启动 → 游戏运行 → OnUnload() → Dispose()
```

---

## API 一：GameStateBus（新）

**纯 C# 读取游戏运行时状态。不需要写 GDScript。**

### 订阅每帧快照

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

### 注册数据读取器

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

// 在构造函数中注册:
mi.RegisterGameStateReader(new MyReader());
```

### EngineObjectReader 方法

| 方法 | 说明 |
|------|------|
| `FindNode("Main/Coins")` | 按 Godot 路径查找节点 |
| `GetChildNames(node)` | 枚举子节点名称（用于发现树结构） |
| `ReadScriptProp(node, "propName")` | 通过 `GDScriptInstance::get` 读 GDScript 变量 |

### GameStateSnapshot

```csharp
public record GameStateSnapshot
{
    long TickCount;           // 帧时间戳
    float Delta;              // idle delta
    Dictionary<string, object?> Extra;  // 扩展数据
}
```

---

## API 二：Patch（语法安全增强版）

### 声明式 `[Patch]`

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

| 属性 | 位置 | 签名 |
|------|------|------|
| `[Prefix]` | 函数体之前 | `static string Code()` |
| `[Postfix]` | 函数体之后 | `static string Code()` |
| `[Replace]` | 替换整个函数体 | `static string Code(string original)` |

### EmbeddedGd：消灭字符串转义

**不要再在 C# 里拼 GDScript 字符串。** 把 GDScript 写成独立的 `.gd` 文件，作为 `EmbeddedResource`。

项目结构：
```
MyMod/
├── MyMod.csproj          # <EmbeddedResource Include="Patches/gd/*.gd" />
├── Mod.cs
└── Patches/
    ├── CoinsPatch.cs
    └── gd/
        └── coins_replace.gd    ← 纯 GDScript，IDE 语法高亮
```

C# 侧一行调用：
```csharp
[Patch("res://Main.tscn::1", "get_coins")]
static class CoinsPatch
{
    [Replace]
    static string ReplaceCode(string original)
        => EmbeddedGd.Read(typeof(CoinsPatch), "gd.coins_replace.gd");
}
```

`.csproj` 添加：
```xml
<ItemGroup>
  <EmbeddedResource Include="Patches/gd/*.gd" />
</ItemGroup>
```

### 语法自动检查（GdTokenizer）

每次 Patch 后，框架自动对 patched 源码做 tokenizer 级语法检查。
有错误时输出**精确行号**：

```
[WARN] #42 [res://Slot Icon.tscn::1] Patched source has syntax error at line 128: Unclosed string literal
```

设 `GDWEAVE_STRICT_SANDBOX=1` 则在检测到错误时**拒绝写入**，回退原始源码，游戏不会崩。

### 重载循环断路器

如果某个 SourceMod 产生无效 GDScript 导致 Godot 反复重载同一脚本（≥3 次 / 2 秒），框架自动将该路径加入**黑名单**，后续 reload 跳过 Pipeline 直接用原始源码：

```
[LOOP-BREAK] res://Tooltip.tscn::1 reloaded 3 times in 150ms — blacklisting
```

### ISourceMod 低级 API

```csharp
using SlotWeave.Modding;

public class MySourceMod : ISourceMod
{
    public bool ShouldRun(string path) => path == "res://Main.tscn::1";
    public string Modify(string path, string source) => source.Replace("a", "b");
}

mi.RegisterSourceMod(new MySourceMod());
```

---

## IModInterface 完整参考

```csharp
public interface IModInterface
{
    string GameDir { get; }
    string SlotWeaveDir { get; }
    ILogger Logger { get; }
    string[] LoadedMods { get; }

    T ReadConfig<T>() where T : class, new();
    void WriteConfig<T>(T config);

    void RegisterSourceMod(ISourceMod mod);          // Patch API
    void Subscribe<T>(Action<T> handler);            // 事件订阅

    void RegisterGameStateReader(IGameStateReader r);   // GameState API
    void UnregisterGameStateReader(IGameStateReader r);

    void ClearCache();
}
```

---

## 日志

```csharp
var log = mi.Logger.ForContext("SourceContext", "MyMod");
log.Information("Mod loaded");
log.Debug("Path={Path}", mi.GameDir);  // 仅 GDWEAVE_DEBUG=1
log.Warning("Warning message");
log.Error(exception, "Fatal error");
```

---

## 故障排查

| 症状 | 检查 |
|------|------|
| `HostingError(InvalidConfigFile)` | 没装 .NET 8 运行时，用 `run-lbl.bat` 自动安装 |
| 游戏闪退 | `SlotWeave.log` 最后几行 |
| Mod 未生效 | manifest.json 字段名 PascalCase？path/function 匹配？ |
| 控制台无输出 | 设了 `GDWEAVE_CONSOLE=1`？ |
| patched 脚本语法报错 | 设 `GDWEAVE_DUMP_PATCHED=1` 查看源码；用 `EmbeddedGd` 替代字符串拼接 |
| 游戏更新后崩溃 | RVA 可能已变，等待框架更新 |
