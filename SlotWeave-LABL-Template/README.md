# SlotWeave Mod Template · Luck be a Landlord

SlotWeave Mod 快速开发模板，适用于《幸运房东》(Luck be a Landlord)。复制到 `SlotWeave/mods/` 下，改掉 Mod 标识和名称即可开始。

SlotWeave 在 Godot 引擎脚本加载阶段 (`GDScript::reload`) 拦截并注入代码，无需修改游戏文件。

---

## 环境准备

### 1. 安装 .NET 8 SDK

[下载 .NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows x64）。

### 2. 安装 SlotWeave

```
Luck be a Landlord/
├── Luck be a Landlord.exe
├── winmm.dll
└── SlotWeave/
    ├── core/
    │   ├── SlotWeave.dll
    │   └── ...
    ├── mods/               ← Mod 放这里
    └── configs/
```

### 3. 获取游戏源码（GDRE Tools）

开发 Mod 需要查看目标脚本的路径、函数名和源码。

1. 下载 [GDRE Tools](https://github.com/bruvzg/gdsdecomp/releases) (Godot Reverse Engineering)
2. 打开 GDRE Tools，选择游戏的 `.pck` 文件或游戏目录
3. 菜单 "RE Tools" → "Recover project" → "Full Recovery"
4. 反编译后的项目可用 Godot 编辑器打开浏览

脚本路径格式：
- 独立 `.gd` 文件 → `res://scripts/player.gd`
- `.tscn` 内嵌脚本 → `res://Main.tscn::1`（数字 ID 可在反编译后的 .tscn 文件中确认）

### 4. Godot 可视化开发 + 一键生成 Patch（推荐）

在 Godot 编辑器里改代码，比手写 C# verbatim string 直观得多。工作流：

```
1. GDRE Tools 反编译游戏 → source_original/
2. 复制一份 → source_working/，用 Godot 编辑器打开
3. 在 Godot 中编辑脚本、运行测试
4. scripts\gen-patches.bat → 自动 diff → 生成 C# [Patch] 类
5. build.bat → 编译 DLL + 部署到游戏测试
```

**gen-patches.py 做了什么：**

- 遍历 `source_working/` 下所有 `.gd` 文件
- 对比 `source_original/` 中的原始版本
- 函数体有改动 → 生成 `[Replace]` patch
- 每个 `.gd` 文件生成一个对应的 `.cs` 文件到 `Patches/`

**配置 gen-patches.bat：**

```bat
set ORIG=D:\steam\steamapps\common\Luck be a Landlord\source_recovered
set MODI=D:\steam\steamapps\common\Luck be a Landlord\source_working
```

> 需要 Python 3 在 PATH 中。

---

## 快速开始

### 1. 复制模板

```
SlotWeave/mods/MyMod/
├── Mod.csproj
├── Mod.cs
├── manifest.json
├── Patches/
│   ├── PrefixExample.cs
│   └── ReplaceExample.cs
└── bin/                   ← 自动生成
```

### 2. 修改 manifest.json

```json
{
  "Id": "MyMod",
  "AssemblyPath": "MyMod.dll",
  "Dependencies": [],
  "Metadata": {
    "Name": "My Mod",
    "Version": "1.0.0",
    "Author": "",
    "Description": ""
  }
}
```

| 字段 | 说明 |
|------|------|
| `Id` | 唯一标识，必须与文件夹名一致 |
| `AssemblyPath` | DLL 文件名 |
| `Dependencies` | 依赖的其他 Mod ID |
| `Metadata.Version` | 版本变化时缓存自动失效 |

### 3. 构建

```bash
scripts\build.bat
```

脚本会自动编译 DLL + 复制 manifest 到游戏 mods 目录。

### 4. 启动游戏验证

```
set GDWEAVE_DEBUG=1
set GDWEAVE_DUMP_SOURCE=1
set GDWEAVE_DUMP_PATCHED=1
Luck be a Landlord.exe
```

查看 `SlotWeave/SlotWeave.log` 确认 Mod 加载，查看 `SlotWeave/scripts_patched/` 确认 patching 结果。

---

## Patch API 参考

### [Patch] 声明式 API

```csharp
using SlotWeave.Scripting;

// path: 精确路径 / 后缀匹配 / "*" 通配符
// function: 目标函数名

[Patch("res://Main.tscn::1", "get_coins")]
class MoneyPatch
{
    // [Prefix] — 注入到函数体第一行之前
    [Prefix]
    static string Code() => "print('function called!')";

    // [Postfix] — 注入到函数体最后一行之后
    [Postfix]
    static string Code() => """
        coin_label.text = str(coins)
        """;

    // [Replace] — 替换整个函数体
    [Replace]
    static string Code(string original)
        => original.Replace("coins = 0", "coins = 999");
}
```

| 属性 | 注入位置 | 方法签名 |
|------|---------|---------|
| `[Prefix]` | 函数体第一行前 | `static string Code()` |
| `[Postfix]` | 函数体最后一行后 | `static string Code()` |
| `[Replace]` | 替换整个函数体 | `static string Code(string original)` |

#### 路径匹配

| 写法 | 匹配 |
|------|------|
| `"res://Main.tscn::1"` | 精确匹配 |
| `"Main.tscn::1"` | 后缀匹配 |
| `"res://Main.tscn::*"` | Main.tscn 中所有内嵌脚本 |
| `"*"` | 全部脚本 |

> **API 语义差异（重要）**：
>
> | API | 看到的是 | 顺序 | 适用场景 |
> |-----|---------|------|---------|
> | `[Patch]` | **原始源码**（所有 Patch 共享同一份） | 无关 | 独立修改不同函数 |
> | `ISourceMod` | **上一个 Mod 的输出**（串行管线） | 敏感，通过 `Dependencies` 控制 | 需要感知其他 Mod 的改动 |
>
> 如果两个 `[Patch]` 都 `[Replace]` 同一个函数，框架会在日志中输出警告——这是设计约束。正确的做法是拆成 `[Prefix]` + `[Postfix]`，或者改用 `ISourceMod` 声明依赖。

### ISourceMod 低级 API

```csharp
using SlotWeave.Modding;

public class MyMod : ISourceMod
{
    public bool ShouldRun(string path) => path == "res://scripts/player.gd";
    public string Modify(string path, string source)
        => source.Replace("var hp = 10", "var hp = 999");
}

// 在 Mod 构造函数中注册：
modInterface.RegisterSourceMod(new MyMod());
```

### IModInterface 完整 API

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
    void ClearCache();
}
```

---

### 日志

Mod 通过 `IModInterface.Logger` 输出日志到 console 和 `SlotWeave/SlotWeave.log`。

```csharp
public Mod(IModInterface modInterface)
{
    var log = modInterface.Logger.ForContext("SourceContext", "MyMod");

    log.Information("Mod loaded");               // 默认可见
    log.Debug("GameDir = {Path}", modInterface.GameDir); // 仅 GDWEAVE_DEBUG=1
    log.Warning("Unexpected but recoverable");
    log.Error(e, "Fatal — mod may not work");
}
```

| 方法 | 何时可见 |
|------|---------|
| `Debug/Verbose` | 仅 `GDWEAVE_DEBUG=1` |
| `Information` | 默认 |
| `Warning/Error` | 默认 |

> `ForContext("SourceContext", "MyMod")` 给日志加 `[MyMod]` 前缀，方便 grep。

---

## 调试环境变量

| 变量 | 用途 |
|------|------|
| `GDWEAVE_DEBUG` | 输出每次 reload 的路径/长度 |
| `GDWEAVE_DUMP_SOURCE` | 原始源码 dump 到 `SlotWeave/scripts/` |
| `GDWEAVE_DUMP_PATCHED` | patched 后源码 dump 到 `SlotWeave/scripts_patched/` |
| `GDWEAVE_CONSOLE` | 启动控制台窗口 |
| `GDWEAVE_NO_CACHE` | 禁用缓存 |

---

## 故障排查

- **Mod 未生效** → 检查 path/function 是否匹配，开 `GDWEAVE_DEBUG` + `GDWEAVE_DUMP_PATCHED`
- **缩进异常** → 对比 `scripts/`（原始）和 `scripts_patched/`（patched），检查 verbatim string 是否有混入的 tab
- **游戏闪退** → 查看 `SlotWeave/SlotWeave.log`
- **缓存问题** → 删 `SlotWeave/cache/`
