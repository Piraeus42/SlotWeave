# Double Injection Bug Report

> 2026-06-04 | 状态: 未修复，幂等守卫绕过 | 影响: 所有 ISourceMod

---

## 一、现象

同一个 GDScript 资源被 SlotWeave 的 SourceModder 管线处理**两次或更多次**，导致注入代码重复出现。

### 直接证据（来自 `GDWEAVE_DUMP_PATCHED=1` 输出）

```gdscript
# Main.tscn__4.gd  (patched output)

line 2187: # === RNG wrappers (BetterHistoryMod) — Reels ===
line 2188: func _rrr_shuffle(arr):         ← 第一次注入
...
line 2205: # === RNG wrappers (BetterHistoryMod) — Reels ===
line 2206: func _rrr_shuffle(arr):         ← 第二次注入，完全相同的代码块
```

两次注入之间相隔 18 行。第一个块有 `[ReelRngRefSourceMod]` provenance 标记，第二个没有 — 说明第二次调用时 SourceModder 管线收到的 `source` 已经被重置为原始内容（或接近原始）。

---

## 二、根因分析

### 2.1 Godot 侧: 同一 GDScript 资源的多个加载路径

Godot 3.4.4 对 `.tscn` 内嵌脚本有多个内部引用路径。同一段 GDScript 源码可以通过不同的资源路径被触发 reload：

```
物理文件: res://Main.tscn
  ├── 内嵌脚本 SubResource 4  →  路径 A: "res://Main.tscn::4"
  └── 内部资源路径             →  路径 B: "res://.godot/imported/..."  或类似的内部 alias
```

当 Godot 的资源系统检测到脚本需要重新编译时，可能通过**两个不同的 path 字符串**各触发一次 `GDScript::reload()`，但底层指向的是同一个 `GDScript*` 对象（同一块内存）。

### 2.2 SlotWeave 侧: 路径字符串去重失效

**旧代码 (v1)** — 按源码内容去重:

```csharp
// Hooks.cs — 原始逻辑
private readonly HashSet<string> knownPatchedSources = [];

// ReloadDetour 中:
var alreadyPatched = source != null && this.knownPatchedSources.Contains(source);
if (!alreadyPatched) {
    // run pipeline → write back
    this.knownPatchedSources.Add(modified);  // 存 modified，不是 original
}
```

**为什么失效**: `knownPatchedSources` 存的是 `modified`（patch 后的源码），而第二次 reload 时 `SafeReadString` 读到的可能是 Godot 内部重置后的原始源码或部分编译中间态。两者不匹配 → Contains 返回 false → 再次走 pipeline。

**旧代码 (v2)** — 按 path 字符串去重:

```csharp
private readonly HashSet<string> patchedPaths = [];

if (patchedPaths.Contains(path))  return original;  // skip
// ... pipeline ...
patchedPaths.Add(path);
```

**为什么失效**: path A `"res://Main.tscn::4"` ≠ path B `"res://.godot/..."`，两个字符串不同 → `Contains` 返回 false → 各自触发一次 pipeline → 两次注入。

### 2.3 当前修复: GDScript 指针去重

```csharp
// Hooks.cs — 当前代码
private readonly HashSet<IntPtr> patchedScripts = [];

// ReloadDetour 入口:
if (patchedScripts.Contains(gdscript))    // gdscript = ReloadDetour 的第一个参数 (GDScript*)
    return this.reloadHook.Original(gdscript, keepState);  // 跳过

// Pipeline 成功后:
this.patchedScripts.Add(gdscript);
```

**为什么这应该工作**: `gdscript` 参数是 `GDScript*` 指针，由 Godot 引擎传入。同一个 GDScript 对象的指针值在所有路径下都相同。如果 Godot 确实通过两个 path 触发同一个对象，`patchedScripts` 能正确拦截第二次。

### 2.4 为什么仍然需要幂等守卫

即使指针去重已部署，用户报告仍然需要每个 SourceMod 自己做幂等检查才能正常运行。可能原因：

1. **GDScript 对象不是同一个**: Godot 可能真的创建了两个不同的 `GDScript` 实例来表示同一个脚本源码。如果是这样，指针去重也无效。
2. **Pipeline 在第一次调用时修改了源码，但第二次调用前 Godot 重建了 GDScript 对象**: 旧的指针被释放，新的指针不同 → `patchedScripts` 不命中。
3. **ISourceMod.ShouldRun() 的路径匹配问题**: 如果 SourceMod 用 `path.Contains("Main")` 这种宽泛匹配，两个不同 path 都会通过 `ShouldRun`，即使 pipeline 只跑一次，同一个 ISourceMod 也会处理两次"看起来不同"的脚本。

---

## 三、控制流详解

### 单次正常流程

```
Godot 内部触发 reload:
  → GDScript::reload(GDScript* this, bool keep_state)
    → SlotWeave Hook 拦截 (ReloadDetour)
      → SafeReadString(this + 0x248)  → source = "原始源码"
      → SafeReadString(this + 0x250)  → path  = "res://Main.tscn::4"
      ↓
      patchedScripts.Contains(this)  → false (第一次)
      ↓
      SourceModder 管线:
        foreach (ISourceMod mod in mods)
          if (mod.ShouldRun(path))     → "res://Main.tscn::4" 匹配
            modified = mod.Modify(path, modified)
      ↓
      WriteGodotString(this + 0x248, modified)  → 写入引擎内存
      patchedScripts.Add(this)                   → 标记已处理
      ↓
      return original.reload(this, keep_state)   → Godot 编译新源码
```

### 双重注入流程（有 bug 时）

```
第一次 reload:
  path = "res://Main.tscn::4"
  GDScript* = 0xABCD
  patchedScripts.Contains(0xABCD) → false → 跑 pipeline → 写入 → Add(0xABCD)

=== Godot 内部: 通过另一个资源路径再次触发 ===

第二次 reload:
  path = "res://.godot/imported/Main.tscn::4"    ← 不同的 path 字符串!
  GDScript* = 0xABCD  (还是同一个对象)            ← 指针相同?
  patchedScripts.Contains(0xABCD) → true → 跳过 ✅

但如果 GDScript* = 0xWXYZ (不同对象!):
  patchedScripts.Contains(0xWXYZ) → false → 又跑一次 pipeline ❌
```

### 幂等守卫的补救

```csharp
// 每个 ISourceMod 自行检查
public string Modify(string path, string source)
{
    if (source.Contains("func _rrr_shuffle"))  // 已经注入过了
        return source;                           // 什么都不做
    // ... 注入代码 ...
}
```

这就是为什么"非常麻烦" — **每个 SourceMod 都要自己写幂等检查**，不同 Mod 之间无法共享这个保护。

---

## 四、影响范围

### 已验证会触发此问题的脚本

| 脚本 | path 变体数 | 证据 |
|------|-----------|------|
| `Main.tscn::4` (Reels) | 2+ | patched dump 中重复注入 |
| `Main.tscn::1` | 可能 | 9 个 ops，依赖链复杂 |
| `Main.tscn::6` | 可能 | 类似 |
| `Pop-up.tscn::1` | 可能 | 类似 |

### 对多 Mod 开发的影响

- **Mod A** 注册 ISourceMod → 修改 Main.tscn::4
- **Mod B** 注册 ISourceMod → 也修改 Main.tscn::4
- 每个 SourceMod 都要自己检查"我的代码已经在了吗"
- Mod A 的幂等守卫不认识 Mod B 的注入代码
- 如果两个 Mod 都注入类似的函数名 → 冲突/重复

---

## 五、需要架构师确认的问题

### Q1: 指针去重到底有没有生效？

加一条诊断日志确认:

```csharp
// 在 patchedScripts.Contains(gdscript) 命中时:
this.logger.Warning("[DEDUP] gdscript=0x{X16} path={Path} ALREADY PATCHED — skipping",
    gdscript.ToInt64(), path);
```

如果这条日志从未出现但仍有重复注入 → 指针去重没拦住 → GDScript 对象不是同一个。

如果这条日志出现了但仍有重复注入 → 指针去重在 pipeline 执行之后才生效 → 第一次调用可能比我们想象的早。

### Q2: Godot 是否真的创建了两个 GDScript 对象？

在 IDA 中对 `GDScript::reload` 设断点。观察两次 reload 调用时 `rcx` 寄存器的值:
- 相同 → 同一个对象，指针去重应生效
- 不同 → 两个独立对象，指针去重无效，需要在别的层面去重

### Q3: 触发第二次 reload 的调用链是什么？

在 x64dbg 中对 `GDScript::reload` 设断点，每次命中拉调用栈 (`k`)。对比两次 `reload` 的调用来源是否相同。如果调用链来自 `ScriptServer` vs `ResourceLoader` 不同路径，就知道了。

---

## 六、可能的根本解决方案

### 方案 A: 源码内容哈希去重（修正版）

不是对比 `modified` 存的内容，而是**对比原始源码的哈希**:

```csharp
private readonly HashSet<int> patchedSourceHashes = [];

var sourceHash = source.GetHashCode();  // 原始源码的 hash
if (patchedSourceHashes.Contains(sourceHash))
    return original;
// ... pipeline ...
patchedSourceHashes.Add(sourceHash);
```

即使 path 不同、对象不同，只要原始源码内容一样就跳过。缺点: 如果两个不同的脚本恰好内容相同（可能性极低），会被误判。

### 方案 B: 签名注入标记（推荐）

在 patched 后的源码中注入一条**不可见的注释标记**:

```csharp
const string MARKER = "# GDWEAVE_PATCHED_v2";

// 在 pipeline 之前检查:
if (source != null && source.Contains(MARKER))
    return original;  // 已经被某个版本的 SlotWeave 处理过

// Pipeline 之后:
modified += "\n" + MARKER;
```

无论 path、对象、内容如何变化，只要源码里有这个标记，就跳过。**这是最可靠的方案**。

优点:
- 不依赖指针、路径、哈希
- 跨 Mod 共享 — 无论哪个 Mod 先 patch，标记都存在
- 简单，零维护成本

缺点:
- 在源码中多了一行注释（对 Godot 编译无影响）
- 如果 Mod 的 ISourceMod 主动移除了这行标记，保护失效（但这是恶意行为，不是 bug）

### 方案 C: 脚本级一次性标记

在 GDScript 对象的内存中写入一个自定义 flag。类似 patchedScripts，但不依赖对象指针的持久性:

```csharp
// 在 GDScript 结构体的一个未使用字段中写入标记
Marshal.WriteByte(gdscript + KNOWN_UNUSED_OFFSET, 1);
```

缺点: 需要找到一个安全的未使用偏移，且游戏更新后可能变化。

---

## 七、当前代码位置

| 文件 | 行号 | 内容 |
|------|------|------|
| `Hooks.cs:107` | `patchedScripts` | HashSet\<IntPtr\> 指针去重 |
| `Hooks.cs:173` | guard check | `patchedScripts.Contains(gdscript)` |
| `Hooks.cs:248` | add | `patchedScripts.Add(gdscript)` |
| `BetterHistoryMod/*.cs` | 各 SourceMod | 幂等守卫 (每个 Mod 手动检查) |



先把问题拆清楚。你这两份报告其实是**两个独立的 bug**，但它们的根因是同一个：

- **Problem 1（双重注入）**：管线对同一脚本跑了两次以上，重复 append 注入代码。属于 SlotWeave 框架层的幂等问题。
- **Problem 2（双重替换/自毁）**：`_lfr__lfr_rand_range`、`_scr__sir_rand_range` 这类被破坏的函数名。属于 `String.Replace` 既不幂等也不原子的问题。

共同根因一句话：**替换是盲目的子串替换，输出会被后续规则再次匹配**。幂等守卫只是在症状上打补丁，所以才"非常麻烦"。

下面给一套两层方案，把这两类 bug 从机制上消掉，而不是靠每个 mod 自己防御。

---

## 核心：一个幂等 + 原子的单遍重写器

这是整个方案的关键，**纯 mod 侧、不依赖修改 SlotWeave 就能用**。它有三个性质，正好对应你踩的三个坑：

1. **原子性**：源码只从左到右扫一遍。命中替换后，把替换结果写出并把游标跳到替换区间之后，**输出永远不会被再次扫描**。这直接杀死 Bug #2 的级联（`_scr_rand_range` → `_scr__sir_rand_range`）。
2. **确定性（最长匹配优先）**：同一位置多个规则能命中时，`from` 最长的赢。这样"特定覆盖"和"通用替换"可以共存，**不再依赖书写顺序**。
3. **幂等性（标识符边界感知）**：用 `RenameCall` 注册的规则，只在匹配位置**前一个字符不是标识符字符**（`[A-Za-z0-9_]`）时才命中。因为你注入的前缀都以 `_` 结尾，`_lfr_rand_range(` 里的 `rand_range(` 前面是 `_`，边界检查直接拒绝 → **重复应用 = 无操作**。这就从机制上消掉了 Bug #1 的跨处理器破坏，连幂等守卫都不需要。

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BetterHistoryMod.Rewriting;

/// <summary>
/// Single-pass, longest-match, optionally identifier-boundary-aware rewriter
/// for GDScript source.
///
/// Properties:
///  1. Atomic     – source is scanned once, left to right. A replacement's
///                  output is emitted and the cursor jumps past it, so output
///                  is NEVER re-scanned. Kills the cascade hazard of sequential
///                  String.Replace (Bug #2: _scr_rand_range -> _scr__sir_rand_range).
///  2. Deterministic – when several rules match at one position the LONGEST
///                  "from" wins (ties: registration order). A specific override
///                  beats a generic rule regardless of ordering.
///  3. Idempotent – RenameCall rules match only when the char before the match
///                  is not an identifier char. Every injected prefix ends in '_',
///                  so re-applying the same rules is a no-op. Kills the cross-
///                  processor hazard (Bug #1: _lfr_rand_range -> _lfr__lfr_rand_range)
///                  WITHOUT any manual guard.
/// </summary>
public sealed class GdScriptRewriter
{
    private readonly struct Rule
    {
        public readonly string From;
        public readonly string To;
        public readonly bool   LeftBoundary; // require non-identifier (or BOL) before match
        public Rule(string from, string to, bool leftBoundary)
        {
            From = from; To = to; LeftBoundary = leftBoundary;
        }
    }

    private readonly List<Rule> _rules = new();
    private Rule[] _ordered = Array.Empty<Rule>();
    private bool _compiled;

    /// <summary>Raw literal replacement. No boundary check.
    /// Use only when the replacement does NOT contain the search text.</summary>
    public GdScriptRewriter ReplaceLiteral(string from, string to)
        => Add(from, to, leftBoundary: false);

    /// <summary>Identifier-aware replacement, e.g. "rand_range(" -> "_sir_rand_range(".
    /// Matches only when not preceded by an identifier char, so it never corrupts
    /// an already-prefixed identifier and is safe to apply more than once.</summary>
    public GdScriptRewriter RenameCall(string from, string to)
        => Add(from, to, leftBoundary: true);

    private GdScriptRewriter Add(string from, string to, bool leftBoundary)
    {
        if (string.IsNullOrEmpty(from))
            throw new ArgumentException("from must be non-empty", nameof(from));
        _rules.Add(new Rule(from, to, leftBoundary));
        _compiled = false;
        return this;
    }

    public string Apply(string source)
    {
        if (string.IsNullOrEmpty(source) || _rules.Count == 0)
            return source;
        if (!_compiled)
        {
            // Longest "from" first; OrderByDescending is stable so ties keep
            // registration order.
            _ordered = _rules.OrderByDescending(r => r.From.Length).ToArray();
            _compiled = true;
        }

        var sb = new StringBuilder(source.Length + 64);
        int n = source.Length, i = 0;
        while (i < n)
        {
            Rule matched = default;
            bool found = false;
            foreach (var rule in _ordered)
            {
                int len = rule.From.Length;
                if (i + len > n) continue;
                if (string.CompareOrdinal(source, i, rule.From, 0, len) != 0) continue;
                if (rule.LeftBoundary && i > 0 && IsIdentChar(source[i - 1])) continue;
                matched = rule;
                found = true;
                break;
            }

            if (found)
            {
                sb.Append(matched.To);
                i += matched.From.Length; // jump past — output is never re-scanned
            }
            else
            {
                sb.Append(source[i++]);
            }
        }
        return sb.ToString();
    }

    private static bool IsIdentChar(char c)
        => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
```

经验法则：**任何形如 `X → 含有X的串`（典型就是加前缀）必须用 `RenameCall`**；只有当替换结果不再包含搜索文本时才可以用 `ReplaceLiteral`。

---

## 用它改写你现有的 mod

### SlotIcon（Bug #2，内部顺序自毁）

不再关心顺序，一次性声明所有规则，最长匹配自动让特定覆盖赢：

```csharp
private static readonly GdScriptRewriter SlotIconRewriter = new GdScriptRewriter()
    // specific overrides (literal — output no longer contains the search text)
    .ReplaceLiteral("str(floor(rand_range(0, sfx_total_num)))", "str(_scr_randi_max(sfx_total_num))")
    .ReplaceLiteral("floor(rand_range(-1, 2))",                 "floor(_scr_rand_range(-1, 2))")
    // generic (boundary-aware — safe & idempotent)
    .RenameCall   ("rand_range(",                               "_sir_rand_range(");

public string Modify(string path, string source)
{
    if (source.Contains("func _sir_shuffle")) return source; // injection guard, see note
    source = SlotIconRewriter.Apply(source);
    // ... 注入 helper 函数 ...
    return source;
}
```

扫到 `floor(rand_range(-1, 2))` 时，最长规则（特定覆盖）整段命中并被跳过，内部的 `rand_range(` 根本不会被单独访问 → 不再产生 `_scr__sir_rand_range`。

### Landlord（Bug #1，ISourceMod + `[Replace]` 跨处理器）

最干净的做法是**让 ISourceMod 独占替换，删掉 `[Replace]` 里重复的那条**。如果框架强制 `[Replace]` 必须存在，就让它也走 `RenameCall`——因为边界感知是幂等的，即使被二次应用也不会变成 `_lfr__lfr_rand_range`：

```csharp
// LandlordFinePrintPatch.[Replace]
private static readonly GdScriptRewriter Rw = new GdScriptRewriter()
    .RenameCall("rand_range(", "_lfr_rand_range(");

[Replace]
static string ReplaceCode(string original) => Rw.Apply(original);
// _lfr_rand_range( 里的 rand_range( 前面是 '_' -> 边界拒绝 -> 二次应用无害
```

这样就能扔掉那个 `original.Contains(...) ? original : original` 的丑陋 pass-through。

---

## SlotWeave 层：用内容标记终结"双重注入"（Problem 1）

上面的重写器让 **rename** 幂等了，但 **注入 helper 函数是 append**，append 两次就是两份定义。要彻底解决"管线跑两次"，最可靠的是在框架层加一个**内容内嵌的标记**——它不依赖指针、路径、对象身份，能扛住你报告里 2.1/2.2 描述的所有变体。

关键依据：你的 patched dump 里**两个注入块同时存在**，说明第二次 reload 读到的 `source` 里**确实带着第一次的注入**（否则最终只会剩第二块）。既然第一块在，标记也一定在 → `Contains(marker)` 一定命中。这正是旧的"整串内容比较"失败、而"子串标记"能成的原因。

基于你报告里描述的 `ReloadDetour` 结构（offset/字段名按你的代码确认）：

```csharp
private const string PatchMarker = "# GDWEAVE_PATCHED";

private nint ReloadDetour(nint gdscript, bool keepState)
{
    string? source = SafeReadString(gdscript + SourceOffset); // 0x248
    string? path   = SafeReadString(gdscript + PathOffset);   // 0x250

    // 内容内嵌的幂等守卫：跨 指针/路径/对象 都有效
    if (source != null && source.Contains(PatchMarker))
        return this.reloadHook.Original(gdscript, keepState);

    if (source != null && path != null)
    {
        string modified = source;
        bool changed = false;
        foreach (var mod in this.sourceMods)
        {
            if (!mod.ShouldRun(path)) continue;
            var next = mod.Modify(path, modified);
            if (next != modified) { modified = next; changed = true; }
        }

        if (changed)
        {
            modified += "\n" + PatchMarker + "\n"; // 末尾注释，GDScript 编译无影响
            WriteGodotString(gdscript + SourceOffset, modified);
        }
    }

    return this.reloadHook.Original(gdscript, keepState);
}
```

我**特意删掉了 `patchedScripts`（HashSet<IntPtr>）**，原因是它有 ABA 隐患：一个被释放的 `GDScript*` 指针如果被引擎复用给另一个脚本，指针去重会**错误地跳过**那个新脚本。内容标记没有这个问题，所以它既更鲁棒、又能取代指针去重。这也顺带回答了你报告里的 Q2——指针是否稳定不再重要。

如果你想先确认到底落在哪个分支，可以临时加你报告 Q1 里那条日志，但我预期标记方案会让它根本不再触发。

---

## 多 mod 协作怎么办

框架能保证的：**不会因为替换而互相破坏**（边界感知 + 单遍 + 标记）。

框架不能替你决定的：**语义冲突**。比如 Mod A 和 Mod B 都想接管 `rand_range`——边界感知会让先跑的那个赢，第二个的 rename 不命中，不会崩，但也只有一个 RNG 生效。这是设计取舍，不是字符串框架能仲裁的。

所以约定仍然需要，但只剩一条且很轻：**每个 mod 用互不嵌套的唯一前缀**（`_csr_`、`_sir_`、`_rrr_`、`_lfr_`…），注入的 helper 函数名带前缀。配合上面的标记守卫，重复注入和命名破坏都没了；剩下的只有"两个 mod 想做同一件事"这种真实的设计冲突。

---

## 验证建议

写一个针对 `GdScriptRewriter` 的小测试，重点测**二次应用幂等**和**特定/通用共存**：

```csharp
var rw = new GdScriptRewriter()
    .ReplaceLiteral("floor(rand_range(-1, 2))", "floor(_scr_rand_range(-1, 2))")
    .RenameCall("rand_range(", "_sir_rand_range(");

var once  = rw.Apply(src);
var twice = rw.Apply(once);
Assert.Equal(once, twice);                          // 幂等
Assert.DoesNotContain("_scr__sir_rand_range", once);// 无自毁
Assert.DoesNotContain("_sir__sir_rand_range", twice);
```

---

我可以直接把这套改动落到你的仓库里。需要你给我这几个文件，我按你工程现有风格改：

- `Hooks.cs`（确认 `ReloadDetour` 签名、offset 常量、`sourceMods` 字段名）
- `SlotIconRngSourceMod.cs`、`LandlordRngRefSourceMod.cs`、`LandlordFinePrintPatch.cs`、`ReelRngRefSourceMod.cs` 等用到替换的 SourceMod

拿到后我会加上 `GdScriptRewriter`、逐个迁移替换逻辑、加测试并跑构建。要现在就发文件吗？
