<!-- AI-assisted documentation | SlotWeave LBAL -->

# 内部架构

## 工作原理：为什么 Hook `GDScript::reload()`

《幸运房东》大量使用 `.tscn` 场景内嵌脚本 (SubResource)，核心游戏逻辑直接以文本形式写在 `Main.tscn` 等文件中。Godot 原生的 `ResourceFormatLoaderGDScript` 仅处理独立 `.gd`/`.gdc` 文件，无法拦截内嵌脚本。

`reload()` 是所有脚本编译前的**统一入口**。在此处：

- `this->source` (源码明文) 已填充完成
- `this->path` (资源路径) 已设置
- `GDScriptParser` (语法树解析) 尚未开始

这是修改源码的黄金窗口。

## 项目结构

```
SlotWeave/
├── Hooks.cs                  # 核心：Hook reload()、内存读写、双路径写入
├── SlotWeave.cs                # 入口：初始化、组装管线
├── IMod.cs                   # Mod 入口接口（含生命周期钩子）
├── IModInterface.cs          # Mod 可用的 API
├── Interop.cs                # 内存特征码扫描、Hook 创建
├── MemoryUtils.cs            # 内存读写工具
├── ConsoleFixer.cs           # 控制台修复
├── EventBus.cs               # Loader 内部事件总线
├── ModEvents.cs              # 事件记录类型
├── CacheManager.cs           # 源码 patching 结果缓存
├── Modding/
│   ├── ISourceMod.cs         # 源码级 Mod 接口（低级 API）
│   └── SourceModder.cs       # 源码 Mod 链式执行器
├── Scripting/
│   ├── ScriptInfo.cs         # GDScript 轻量结构解析
│   ├── PatchAttribute.cs     # [Patch]/[Prefix]/[Postfix]/[Replace] 属性
│   ├── PatchManager.cs       # Patch 注册 + 应用引擎
│   └── PatchSourceMod.cs     # PatchManager → ISourceMod 适配器
├── Loader/
│   ├── ModLoader.cs          # Mod 加载、依赖解析、生命周期管理
│   ├── ModInterface.cs       # IModInterface 实现
│   ├── ModLoadContext.cs     # 程序集加载上下文
│   ├── ModManifest.cs        # manifest.json 模型
│   └── LoadedMod.cs          # Mod 数据模型
└── SlotWeave.csproj            # 项目配置 (net8.0)
```

## 源码写入策略（双路径）

| 路径 | 触发条件 | 机制 |
|------|---------|------|
| 原地覆写 | 新内容 ≤ 旧 buffer 容量 | `Marshal.Copy` 直写，更新 size 字段 |
| 引擎扩容 | 新内容 > 旧 buffer 容量 | 调用 Godot 的 `CowData::resize()`，引擎内部 malloc/realloc/free 全权管理 |

引擎扩容流程：`resize(n+1)` → 自读 CowData 指针 → `Marshal.Copy` 写入 + null terminator。所有内存操作由引擎 Memory 分配器统一管理，无跨 allocator 边界问题。

## 已验证的引擎偏移量

| 项目 | 偏移 | 来源 |
|------|------|------|
| `GDScript::source` | `RCX + 0x248` | `source.find("%BASE%")` |
| `GDScript::path` | `RCX + 0x250` | `String basedir = path` |
| `Resource::path` (fallback) | `RCX + 0x108` | `get_path()` |
| `CowData::resize` RVA | `0x14D10` | 反汇编 `sub_140014D10(&v109, 0)` |
| `GDScript::reload()` 特征码 | 见 `docs/patterns.txt` | 函数 prologue |

基于 Godot 3.4.4 custom_build `419e713a2`。游戏更新后需重新验证。

## 调试

- **日志**：`SlotWeave/SlotWeave.log`，设置 `GDWEAVE_DEBUG` 获取 Verbose 级别
- **源码 dump**：设置 `GDWEAVE_DUMP_SOURCE`，所有经过 reload() 的脚本保存到 `SlotWeave/scripts/`
- **崩溃排查**：查看日志最后几行，通常是 Mod 自身逻辑异常或修改后的源码语法错误
- **缓存目录**：`SlotWeave/cache/`，版本变更自动清除，也可手动删除
- **PatchDump**：设置 `GDWEAVE_DEBUG` 后，每次 Patch 操作输出 `[某Path] (N ops)` 及字节变化
