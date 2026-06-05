# SlotWeave · Luck be a Landlord

Godot 3.4.4 运行时 Mod 注入框架，针对 [*《幸运房东》*](https://store.steampowered.com/app/1404850/Luck_be_a_Landlord/)。

<div align="center">

[English](https://github.com/Piraeus42/SlotWeave/blob/main/README.md) · **简体中文**

</div>

---

## 能做什么

SlotWeave 提供**两套互补 API**，无需 Unity、Mono 或 IL2CPP，直接对接 Godot 原生运行时：

| API | 方式 | 适用场景 |
|-----|------|----------|
| **GameStateBus** | 纯 C# 直读引擎内存 | 仪表盘、浮层、数据采集 |
| **Patch** | GDScript 源码注入：`[Prefix]` / `[Postfix]` / `[Replace]` | 修改游戏行为、重定向函数调用 |

---

## 为什么值得用

- **自动语法校验** — 每次 patch 后 tokenizer 自动检查，语法错误给出精确行号，不会直接崩溃。
- **重载循环断路器** — 无效输出导致引擎反复重载？2 秒内自动拉黑。
- **Sentinel 自动守卫** — 框架层防重复注入，mod 不再需要手写"我已经运行过了吗"。
- **EmbeddedGd** — GDScript 写在 `.gd` 文件里，IDE 语法高亮，告别 C# 字符串转义地狱。
- **EventBus + Cache** — 订阅加载事件。源码 SHA256 缓存，不重复 patch。
- **Provenance 追踪** — dump patched 脚本时标注每行来自哪个 mod，方便多 mod 调试。

> **架构**: [`docs/architecture.md`](docs/architecture.md) · **Mod 开发指南**: [`docs/mod-development-guide.md`](docs/mod-development-guide.md)

---

## 安装

1. `winmm.dll` → 游戏根目录（与 `Luck be a Landlord.exe` 同级）
2. `SlotWeave/` 文件夹 → 同目录
3. Mod → `SlotWeave/mods/<ModId>/`（每个 Mod 一个文件夹，内含 `manifest.json` + `.dll`）
4. 启动游戏 — 需安装 **.NET 8 Desktop Runtime**

```
Luck be a Landlord/
├── Luck be a Landlord.exe
├── winmm.dll                ← 代理注入 loader
├── SlotWeave/
│   ├── core/                ← 框架运行时
│   ├── mods/                ← 你的 Mod
│   ├── configs/             ← 自动生成
│   └── SlotWeave.log        ← 调试日志
```

### 环境变量

| 变量 | 用途 |
|------|------|
| `GDWEAVE_CONSOLE=1` | 分配控制台窗口 |
| `GDWEAVE_DEBUG=1` | Verbose 日志 |
| `GDWEAVE_DUMP_SOURCE=1` | dump 原始脚本到 `scripts/` |
| `GDWEAVE_DUMP_PATCHED=1` | dump patched 脚本（附带 provenance 标注） |
| `GDWEAVE_STRICT_SANDBOX=1` | 语法错误时拒绝写入，回退原始源码 |
| `GDWEAVE_NO_CACHE=1` | 禁用 patch 缓存 |

### 从源码构建

```bash
dotnet build -c Debug -p:SlotWeavePath="<YOUR_GAME_PATH>"
cargo build --release   # Rust loader
```

---

## Mod 开发

每个 Mod 是一个 .NET 8 程序集，附带 `manifest.json`：

```json
{
  "Id": "MyMod",
  "AssemblyPath": "MyMod.dll",
  "Dependencies": [],
  "Metadata": { "Name": "My Mod", "Version": "1.0.0", "Author": "Me" }
}
```

**字段名必须 PascalCase。** 生命周期：`OnLoad()` → `OnInitialize()` → patches 应用 → 游戏运行 → `OnUnload()` → `Dispose()`。

> **完整指南**: [`docs/mod-development-guide.md`](docs/mod-development-guide.md) — 覆盖 Sentinel、Priority、ReplaceHelper 以及 `[Patch]`/`ISourceMod` 交互规则。

---

## API 一：GameStateBus

**纯 C# 读游戏运行时状态，不需要写一行 GDScript。**

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

注册自定义读取器：

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

| 方法 | 说明 |
|------|------|
| `FindNode("Main/Coins")` | 按 Godot 路径查找节点 |
| `GetChildNames(node)` | 枚举子节点（用于发现树结构） |
| `ReadScriptProp(node, "propName")` | 通过 `GDScriptInstance::get` 读 GDScript 变量 |

---

## API 二：Patch

**声明式属性注入 GDScript。**

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

### EmbeddedGd

GDScript 写成 `.gd` 文件，消灭 C# 字符串转义：

```
MyMod/Patches/gd/coins_replace.gd   ← 纯 GDScript，IDE 语法高亮
```

```csharp
[Replace]
static string ReplaceCode(string original)
    => EmbeddedGd.Read(typeof(CoinsPatch), "gd.coins_replace.gd");
```

### ISourceMod

底层源码变换 API，自带守卫和优先级：

```csharp
public class MySourceMod : ISourceMod
{
    public string? Sentinel => "func _my_helper"; // 框架自动防重复注入
    public int Priority => 10;                    // 优先于低优先级 mod 执行
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

### 日志

```csharp
var log = mi.Logger.ForContext("SourceContext", "MyMod");
log.Information("Mod loaded");
log.Debug("Path={Path}", mi.GameDir);  // 仅 GDWEAVE_DEBUG=1
log.Warning("Something unusual");
log.Error(exception, "Fatal error");
```

---

## 故障排查

| 症状 | 检查 |
|------|------|
| `HostingError(InvalidConfigFile)` | 未安装 .NET 8 Desktop Runtime |
| 游戏闪退 | `SlotWeave.log` 最后几行 |
| Mod 未生效 | manifest 字段名 PascalCase？path/function 匹配？ |
| 控制台无输出 | 设了 `GDWEAVE_CONSOLE=1`？ |
| patched 脚本语法报错 | `GDWEAVE_DUMP_PATCHED=1`；用 `EmbeddedGd` |
| 游戏更新后崩溃 | RVA 可能已变 |

---

## 兼容性与法律说明

**版本绑定**于 Godot 3.4.4 build `419e713a2`。游戏更新可能导致 RVA 偏移失效。

> ## ⚖️ 内容政策
>
> **SlotWeave 及基于本框架开发的所有官方 Mod：**
>
> - ❌ **不**包含、捆绑或重新分发**任何**游戏原始源代码
> - ❌ **不**包含、捆绑或重新分发**任何**游戏原始美术、音频或数据资产
> - ❌ **不**在磁盘上修改或 patch 游戏二进制文件
> - ✅ 完全通过**运行时注入**运作 — Mod 在加载时于内存中应用
> - ✅ Modder 代码 **100% 原创** — 仅分发 modder 编写的变换逻辑
> - ✅ 游戏资产和原始脚本始终保留在游戏安装目录中，绝不复制或重新打包
>
> **这是有意的、永久的设计选择。** 框架在运行时注入行为，绝不附带游戏的任何知识产权。Mod 是变换器，不是衍生品。

---

## 致谢

SlotWeave 是 [GDWeave](https://github.com/NotNite/GDWeave)（作者 [NotNite](https://github.com/NotNite)）的独立 fork。沿用上游相同的许可条款。
