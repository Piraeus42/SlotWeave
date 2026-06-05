<!-- AI-assisted documentation | SlotWeave LBAL -->

# EventBus 与缓存系统

## Loader EventBus

内部事件系统，Mod 可通过 `IModInterface.Subscribe<T>()` 订阅 Loader 运行时事件。纯 C# 实现，不跨 FFI 边界。

### 事件类型

| 事件 | 触发时机 |
|------|---------|
| `ModEvents.ModLoaded` | Mod 程序集加载并 `OnLoad()` 完成后 |
| `ModEvents.ScriptPatched` | 每个脚本完成 SourceModder 管线后 |
| `ModEvents.CacheHit` | 缓存命中，跳过 SourceModder 管线 |
| `ModEvents.CacheStored` | patching 结果写入缓存 |
| `ModEvents.LoaderPhase` | 加载器生命周期阶段 (`Starting` / `Ready`) |

### 数据结构

```csharp
public record ScriptPatched(string Path, int OriginalLength, int PatchedLength, bool Modified);
public record ModLoaded(string ModId, string Version);
public record CacheHit(string ScriptPath);
public record CacheStored(string ScriptPath);
public record LoaderPhase(string Phase); // "Starting" | "Ready"
```

### 使用示例

```csharp
modInterface.Subscribe<ModEvents.ScriptPatched>(e => {
    modInterface.Logger.Information(
        "[{Path}] {Orig} -> {New} chars, modified={Mod}",
        e.Path, e.OriginalLength, e.PatchedLength, e.Modified);
});
```

### 设计约束

- 所有 handler 包在 try-catch 中，单个 handler 异常不影响其他订阅者
- `Subscribe` 是线程安全的（ConcurrentDictionary）
- 事件在调用线程同步触发，不做异步分发

## 缓存系统

### 缓存 Key

```
SHA256(原始源码 + \0 + 排序后的 Mod ID 列表 + \0 + Mod 版本列表)
```

### 存储

- 内存层：`Dictionary<string, string>`，会话内零延迟
- 磁盘层：`SlotWeave/cache/<hex_key>`，JSON 格式，跨启动持久化
- 无需配置，自动生效

### 缓存条目格式

```json
{
  "Key": "a1b2c3d4e5f6...",
  "Patched": "extends Node2D\n..."
}
```

### 生命周期

```
reload() 触发
  ↓
ComputeKey(source, modVersions)
  ↓
内存命中？ → 是 → 使用缓存 ✓
  ↓ 否
磁盘命中？ → 是 → 加载到内存 → 使用缓存 ✓
  ↓ 否
运行 SourceModder 管线
  ↓
结果存入内存 + 磁盘
  ↓
返回 patched source
```

### 失效条件

以下任一变化自动重新 patching：
- 源码内容变化
- Mod ID 列表变化（新增/删除 Mod）
- Mod 版本号变化（`manifest.json` 中的 `Metadata.Version`）

### 手动清除

```csharp
modInterface.ClearCache(); // 删除全部缓存
```

或直接删除 `SlotWeave/cache/` 目录。

### 统计

每 50 次 reload 在 Debug 级别输出命中率：`Cache stats: N hits / M misses`。
